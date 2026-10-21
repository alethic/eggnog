using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Eggnog.Tests.Support;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// Runs a <see cref="RandomProgram"/> on Eggnog at the root, through the internal untyped layer, semi-naive or with
    /// every rule's naive twin (the internal <c>RunOptions.SemiNaive</c>), and renders what each command observed as
    /// <see cref="ReferenceInterpreter.Execute(DiffCommand)"/> does.
    /// </summary>
    internal sealed class EggnogDriver
    {

        readonly RandomProgram program;
        readonly bool semiNaive;
        readonly EGraph graph;
        readonly Dictionary<DiffType, ColumnType> types = new();
        readonly Dictionary<DiffTable, TableHandle> tables = new();
        readonly Ruleset[] rulesets;
        readonly List<Query> queries = new();
        readonly List<DiffRule> specs = new();
        readonly List<Node> handles = new();

        /// <summary>
        /// Defines the program's tables, rulesets and queries in a new graph.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="semiNaive">Whether runs are semi-naive (the default) or use naive twins.</param>
        public EggnogDriver(RandomProgram program, bool semiNaive)
        {
            this.program = program;
            this.semiNaive = semiNaive;
            graph = new EGraph();
            graph.DefineSort<Node>("Node");
            types[DiffType.I64] = graph.Values.TypeOf<long>();
            types[DiffType.Str] = graph.Values.TypeOf<string>();
            types[DiffType.IntSet] = graph.Values.TypeOf<Set<int>>();
            types[DiffType.Node] = graph.Values.TypeOf<Node>();
            foreach (var table in program.Tables)
            {
                var keys = table.Keys.Select(k => types[k]).ToArray();
                tables[table] = table.Kind switch
                {
                    TableKind.Relation => graph.DefineRelation(table.Name, keys),
                    TableKind.Constructor => graph.DefineConstructor(table.Name, keys, types[DiffType.Node]),
                    _ => graph.DefineFunction(table.Name, keys, types[table.Output!.Value], table.Lattice switch
                    {
                        DiffLattice.Max => ValueLattice.Max<long>(),
                        DiffLattice.Intersect => ValueLattice.Intersect<int>(),
                        _ => null,
                    }),
                };
            }

            rulesets = new Ruleset[program.Rulesets];
            for (var i = 0; i < rulesets.Length; i++)
                rulesets[i] = graph.DefineRuleset("rs" + i.ToString(CultureInfo.InvariantCulture));

            foreach (var query in program.Queries)
                Add(query);
        }

        /// <summary>
        /// Gets the graph.
        /// </summary>
        public EGraph Graph => graph;

        /// <summary>
        /// Gets the number of rows of every table: the differential stops a program that grows too large.
        /// </summary>
        public long TotalRows => graph.Tables.Sum(t => graph.Root.CountRows(t));

        /// <summary>
        /// Runs a command and renders what it observed.
        /// </summary>
        /// <param name="command">The command.</param>
        /// <returns>The observation.</returns>
        public string Execute(DiffCommand command)
        {
            var root = graph.Root;
            switch (command.Kind)
            {
                case DiffCommandKind.Assume:
                    root.Assume(command.Facts.Select(f => Fact(f, null)).ToArray());
                    return "assume\n" + Snapshot();
                case DiffCommandKind.Make:
                    handles.Add(root.Make((ConstructorTerm<Node>)Term(command.Term!, null)));
                    return "make\n" + Snapshot();
                case DiffCommandKind.Union:
                {
                    var distinct = root.Union(handles[command.First], handles[command.Second]);
                    return "union " + (distinct ? "distinct" : "equal") + "\n" + Snapshot();
                }
                case DiffCommandKind.DefineRule:
                    DefineRule(command.Rule!);
                    return "define " + command.Rule!.Name;
                case DiffCommandKind.DefineQuery:
                    Add(command.Rule!);
                    return "define query " + command.Rule!.Name;
                case DiffCommandKind.Run:
                {
                    var report = root.Run(Schedule(command.Schedule!), new RunOptions { IterationLimit = command.First, SemiNaive = semiNaive });
                    return $"run iterations={report.Iterations} stop={report.StopReason} saturated={report.Saturated} changed={report.Changed}\n" + Snapshot();
                }
                default:
                    return Query(command.First, command.Mode);
            }
        }

        /// <summary>
        /// Renders every table's rows (<see cref="Scope.Rows(TableHandle)"/>), named, sorted, in declaration order.
        /// </summary>
        /// <returns>The rendering.</returns>
        public string Snapshot()
        {
            var names = Names();
            var builder = new StringBuilder();
            foreach (var table in program.Tables)
            {
                var rows = graph.Root.Rows(tables[table]).Select(r => DiffValues.Row(table, r.Keys.Select((k, i) => Cell(k, table.Keys[i])).ToArray(),
                    r.Output is { } output ? Cell(output, table.Output!.Value) : null, names));
                builder.Append(table.Name).Append(": ").AppendJoin(" | ", rows.OrderBy(r => r, StringComparer.Ordinal)).Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>
        /// Names every class by its least ground term, from the constructors' rows.
        /// </summary>
        Func<long, string> Names()
        {
            var rows = new List<(DiffTable, object[], long)>();
            foreach (var table in program.Tables.Where(t => t.Kind == TableKind.Constructor))
                foreach (var row in graph.Root.Rows(tables[table]))
                    rows.Add((table, row.Keys.Select((k, i) => Cell(k, table.Keys[i])).ToArray(), ((ClassId)row.Output!).Cell));

            var names = TermNamer.Name(rows);
            return id => names.TryGetValue(id, out var name) ? name : "#" + id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A decoded cell as the differential compares it: a class as its id, a value as itself.
        /// </summary>
        static object Cell(object value, DiffType type) => type switch
        {
            DiffType.Node => value is ClassId id ? (long)id.Cell : (long)((Node)value).Id.Cell,
            _ => value,
        };

        /// <summary>
        /// Defines a rule in its ruleset.
        /// </summary>
        void DefineRule(DiffRule rule)
        {
            rulesets[rule.Ruleset].DefineRule(rule.Name, b =>
            {
                var variables = new Dictionary<string, Term>(StringComparer.Ordinal);
                foreach (var (name, type) in rule.Variables)
                    variables[name] = type == DiffType.Node ? b.Var<Node>(name) : b.Var(types[type], name);

                if (rule.Naive)
                    b.Naive();

                foreach (var fact in rule.Body)
                    b.When(Fact(fact, variables));

                foreach (var item in rule.Head)
                {
                    switch (item.Kind)
                    {
                        case DiffHeadKind.Then:
                            b.Then(Fact(item.Fact!, variables));
                            break;
                        case DiffHeadKind.Make:
                            variables[item.Name!] = b.Make(item.Name!, (ConstructorTerm<Node>)Term(item.Left!, variables));
                            break;
                        default:
                            b.Rewrite((Term<Node>)Term(item.Left!, variables), (Term<Node>)Term(item.Right!, variables));
                            break;
                    }
                }
            });
        }

        /// <summary>
        /// Defines a query, which takes the next query number.
        /// </summary>
        void Add(DiffRule query)
        {
            specs.Add(query);
            queries.Add(DefineQuery(query));
        }

        /// <summary>
        /// Defines a query.
        /// </summary>
        Query DefineQuery(DiffRule query) => graph.DefineQuery(query.Name, q =>
        {
            var variables = new Dictionary<string, Term>(StringComparer.Ordinal);
            foreach (var (name, type) in query.Variables)
                variables[name] = type == DiffType.Node ? q.Var<Node>(name) : q.Var(types[type], name);

            foreach (var fact in query.Body)
                q.When(Fact(fact, variables));
        });

        /// <summary>
        /// Asks a query and renders its answer.
        /// </summary>
        string Query(int index, DiffQueryMode mode)
        {
            var query = queries[index];
            var spec = specs[index];
            switch (mode)
            {
                case DiffQueryMode.Any:
                    return $"query {spec.Name} any={graph.Root.Any(query)}";
                case DiffQueryMode.Count:
                    return $"query {spec.Name} count={graph.Root.Count(query)}";
                default:
                {
                    var tuples = new List<object[]>();
                    graph.Root.ForEachMatch(query, m =>
                    {
                        var tuple = new object[spec.Variables.Count];
                        for (var i = 0; i < tuple.Length; i++)
                            tuple[i] = Cell(m.Get(query.Variables[i]), spec.Variables[i].Type);

                        tuples.Add(tuple);
                        return true;
                    });

                    var names = Names();
                    var rendered = tuples.Select(t => string.Join(" ", t.Select((c, i) => DiffValues.Format(c, spec.Variables[i].Type, names))))
                        .OrderBy(t => t, StringComparer.Ordinal);
                    return $"query {spec.Name} count={tuples.Count}: " + string.Join(" | ", rendered);
                }
            }
        }

        /// <summary>
        /// A schedule.
        /// </summary>
        Schedule Schedule(DiffSchedule schedule) => schedule.Kind switch
        {
            DiffScheduleKind.Run => Eggnog.Schedule.Run(schedule.Rulesets.Select(r => rulesets[r]).ToArray()),
            DiffScheduleKind.Saturate => Eggnog.Schedule.Saturate(schedule.Steps.Select(Schedule).ToArray()),
            DiffScheduleKind.Repeat => Eggnog.Schedule.Repeat(schedule.Times, schedule.Steps.Select(Schedule).ToArray()),
            DiffScheduleKind.Sequence => Eggnog.Schedule.Sequence(schedule.Steps.Select(Schedule).ToArray()),
            _ => Eggnog.Schedule.Until(Schedule(schedule.Steps[0]), schedule.Condition.Select(f => Fact(f, null)).ToArray()),
        };

        /// <summary>
        /// A fact, through the untyped layer: <c>RelationHandle.Apply</c>, <c>Fact.Equal</c>, <c>Fact.NotEqual</c>.
        /// </summary>
        Fact Fact(DiffFact fact, Dictionary<string, Term>? variables) => fact.Kind switch
        {
            DiffFactKind.Relation => ((RelationHandle)tables[fact.Table!]).Apply(fact.Arguments.Select(a => Term(a, variables)).ToArray()),
            DiffFactKind.Equal => Eggnog.Fact.Equal(Term(fact.Left!, variables), Term(fact.Right!, variables)),
            DiffFactKind.NotEqual => Eggnog.Fact.NotEqual(Term(fact.Left!, variables), Term(fact.Right!, variables)),
            DiffFactKind.Filter => new FilterFact(Term(fact.Left!, variables)),
            _ => Eggnog.Fact.Exists(Term(fact.Left!, variables)),
        };

        /// <summary>
        /// A term, through the untyped layer: <c>Term.Constant</c>, <c>FunctionHandle.Apply</c>,
        /// <c>Compute.Of(Primitive, …)</c>; a constructor application is a <see cref="ConstructorTerm{TSort}"/>, so it
        /// can be made or rewritten.
        /// </summary>
        Term Term(DiffTerm term, Dictionary<string, Term>? variables)
        {
            switch (term.Kind)
            {
                case DiffTermKind.Var:
                    return variables![term.Name!];
                case DiffTermKind.Const:
                    return term.Type == DiffType.Node ? (Term<Node>)handles[(int)term.Value!] : Eggnog.Term.Constant(types[term.Type], term.Value!);
                case DiffTermKind.App:
                {
                    var arguments = term.Arguments.Select(a => Term(a, variables)).ToArray();
                    var table = tables[term.Table!];
                    return table is ConstructorHandle constructor ? new ConstructorTerm<Node>(constructor, arguments) : ((FunctionHandle)table).Apply(arguments);
                }
                default:
                    return Compute.Of(term.Primitive!.Create(), term.Arguments.Select(a => Term(a, variables)).ToArray());
            }
        }

    }

}
