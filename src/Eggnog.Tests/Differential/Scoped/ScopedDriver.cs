using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Eggnog.Tests.Support;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// Runs a <see cref="ScopedProgram"/> on Eggnog with scopes as contexts, through the internal untyped layer (D13): the
    /// root, children with tags and local rules, scoped atoms over mirrors, host writes, runs, freezes, and checks that
    /// render every scope's view.
    /// </summary>
    internal sealed class ScopedDriver
    {

        readonly ScopedProgram program;
        readonly EGraph graph;
        readonly ColumnType[] types;
        readonly TableHandle[] tables;
        readonly ScopeTag[] tags;
        readonly Ruleset rules;
        readonly List<Scope> scopes = new();
        readonly List<Query> queries = new();
        readonly Node keep;

        /// <summary>
        /// A filter that keeps every <c>long</c>: it changes nothing a rule derives, but it uses its variable, so the
        /// engine counts distinct full bindings (§8.2).
        /// </summary>
        static readonly Primitive<long, bool> KeepLong = new("keep", static _ => true, false);

        /// <summary>
        /// A filter that keeps every <c>string</c>, as <see cref="KeepLong"/>.
        /// </summary>
        static readonly Primitive<string, bool> KeepString = new("keep", static _ => true, false);

        /// <summary>
        /// Defines the program's tables, tags, global rules and queries in a new graph.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="inheritWatermarks">The graph's <c>InheritWatermarks</c> switch.</param>
        public ScopedDriver(ScopedProgram program, bool inheritWatermarks)
        {
            this.program = program;
            graph = new EGraph { InheritWatermarks = inheritWatermarks };
            graph.DefineSort<Node>("Node");
            types = [graph.Values.TypeOf<long>(), graph.Values.TypeOf<string>(), graph.Values.TypeOf<Node>()];
            tables = new TableHandle[program.Tables.Length];
            for (var i = 0; i < tables.Length; i++)
            {
                var spec = program.Tables[i];
                var name = "T" + i.ToString(CultureInfo.InvariantCulture);
                var keys = spec.StringKeys.Select(s => s ? types[1] : types[0]).ToArray();
                tables[i] = spec.Kind switch
                {
                    ScopedTableKind.Relation => graph.DefineRelation(name, keys),
                    ScopedTableKind.Function => graph.DefineFunction(name, keys, types[0], spec.Max ? ValueLattice.Max<long>() : ValueLattice.Min<long>()),
                    ScopedTableKind.Constructor => graph.DefineConstructor(name, keys, types[2]),
                    _ => graph.DefineRelation(name, [types[2]]),
                };
            }

            // The class the keep disequality compares a class variable with: made at the root before anything else, and held
            // by no table the program reads, so the disequality never drops a match.
            var keeper = graph.DefineConstructor("Keep", [], types[2]);
            keep = graph.Root.Make(new ConstructorTerm<Node>(keeper, []));

            tags = new ScopeTag[ScopedProgram.TagCount];
            for (var i = 0; i < tags.Length; i++)
                tags[i] = graph.DefineScopeTag("t" + i.ToString(CultureInfo.InvariantCulture));

            rules = graph.DefineRuleset("rules");
            foreach (var rule in program.Globals)
                rules.DefineRule(rule.Name, b => Build(b, rule));

            foreach (var query in program.Queries)
                queries.Add(graph.DefineQuery(query.Name, q => BuildQuery(q, query)));

            // Every mirror exists before any scope freezes: a first scoped read after a tagged scope froze is refused.
            for (var i = 0; i < tables.Length; i++)
            {
                if (program.Tables[i].IsValueOnly == false)
                    continue;

                for (var j = 0; j < tags.Length; j++)
                {
                    var table = i;
                    var tag = j;
                    graph.DefineQuery($"mirror {i} {j}", q =>
                    {
                        var spec = program.Tables[table];
                        var keys = spec.StringKeys.Select((s, k) => q.Var(s ? types[1] : types[0], "k" + k.ToString(CultureInfo.InvariantCulture))).ToArray();
                        if (spec.Kind == ScopedTableKind.Relation)
                            q.When(((RelationHandle)tables[table]).InScope(tags[tag]).Apply(keys));
                        else
                            q.When(Fact.Equal(((FunctionHandle)tables[table]).InScope(tags[tag]).Apply(keys), q.Var(types[0], "v")));
                    });
                }
            }

            scopes.Add(graph.Root);
        }

        /// <summary>
        /// Gets the graph.
        /// </summary>
        public EGraph Graph => graph;

        /// <summary>
        /// Gets the matches every run reported, summed over rules: what watermark inheritance saves.
        /// </summary>
        public long Matches { get; private set; }

        /// <summary>
        /// Gets the scopes, by number.
        /// </summary>
        public IReadOnlyList<Scope> Scopes => scopes;

        /// <summary>
        /// Runs a command; a check returns every scope's rendering, by number.
        /// </summary>
        /// <param name="command">The command.</param>
        /// <param name="named">Whether a check renders classes by name (<see cref="ScopeFacts"/>), for programs with
        /// constructors; otherwise it renders the value tables only.</param>
        /// <returns>The renderings of a check; <c>null</c> otherwise.</returns>
        public Dictionary<int, string>? Execute(ScopedCommand command, bool named)
        {
            switch (command.Kind)
            {
                case ScopedCommandKind.Create:
                {
                    var parent = scopes[command.Parent];
                    var scope = parent.CreateChild(command.Tag >= 0 ? tags[command.Tag] : null);
                    scopes.Add(scope);
                    foreach (var rule in command.Rules)
                        scope.DefineRule(rules, rule.Name, b => Build(b, rule));

                    return null;
                }
                case ScopedCommandKind.Assume:
                    scopes[command.Scope].Assume(command.Facts.Select(f => Atom(f, null, -1)).ToArray());
                    return null;
                case ScopedCommandKind.Run:
                    Saturate(scopes[command.Scope]);
                    return null;
                case ScopedCommandKind.Freeze:
                    Saturate(scopes[command.Scope]);
                    scopes[command.Scope].Freeze();
                    return null;
                default:
                {
                    foreach (var scope in scopes)
                        if (scope.IsFrozen == false)
                            Saturate(scope);

                    var renderings = new Dictionary<int, string>();
                    for (var i = 0; i < scopes.Count; i++)
                        renderings[i] = Render(i, named);

                    return renderings;
                }
            }
        }

        /// <summary>
        /// Saturates a scope's rules and counts their matches.
        /// </summary>
        void Saturate(Scope scope)
        {
            var report = scope.Run(Schedule.Saturate(rules), new RunOptions { IterationLimit = 200 });
            if (report.StopReason != RunStopReason.Completed)
                throw new InvalidOperationException($"Scope {scope.Path} did not saturate: {report.StopReason}.");

            Matches += report.Rules.Values.Sum(r => r.Matches);
        }

        /// <summary>
        /// Renders a scope: its value tables' rows (or every fact, named), its scoped reads of each value table under each
        /// tag that resolves from it, and the answers of the queries whose tags resolve from it.
        /// </summary>
        string Render(int index, bool named)
        {
            var scope = scopes[index];
            var builder = new StringBuilder();
            if (named)
            {
                foreach (var line in ScopeFacts.Of(scope))
                    builder.Append(line).Append('\n');
            }

            for (var i = 0; i < tables.Length; i++)
            {
                if (program.Tables[i].IsValueOnly == false)
                    continue;

                if (named == false)
                    builder.Append(Rows(tables[i].Name, scope.Rows(tables[i]))).Append('\n');

                for (var j = 0; j < tags.Length; j++)
                {
                    if (Resolves(index, j) == false)
                        continue;

                    var view = program.Tables[i].Kind == ScopedTableKind.Relation ? (TableHandle)((RelationHandle)tables[i]).InScope(tags[j]) : ((FunctionHandle)tables[i]).InScope(tags[j]);
                    builder.Append(tables[i].Name).Append("@t").Append(j).Append(' ').Append(Rows(tables[i].Name, scope.Rows(view))).Append('\n');
                }
            }

            for (var q = 0; q < queries.Count; q++)
            {
                if (program.Queries[q].Tags.All(t => Resolves(index, t)) == false)
                    continue;

                var variables = queries[q].Variables;
                var answers = new List<string>();
                scope.ForEachMatch(queries[q], m =>
                {
                    var parts = new string[variables.Count];
                    for (var v = 0; v < parts.Length; v++)
                        parts[v] = Format(m.Get(variables[v]));

                    answers.Add(string.Join(" ", parts));
                    return true;
                });
                answers.Sort(StringComparer.Ordinal);
                builder.Append(program.Queries[q].Name).Append(" count=").Append(scope.Count(queries[q])).Append(": ").AppendJoin(" | ", answers).Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>
        /// Gets whether a tag resolves from a scope.
        /// </summary>
        bool Resolves(int scope, int tag) => ScopeResolver.TryResolve(scopes[scope], tags[tag]) is not null;

        /// <summary>
        /// Renders rows as the oracle does: <c>T0: (T0 1 "a") | (T0 2 "b")</c>, sorted.
        /// </summary>
        static string Rows(string table, IReadOnlyList<TableRow> rows) =>
            table + ": " + string.Join(" | ", rows.Select(r => Row(table, r.Keys, r.Output)).OrderBy(r => r, StringComparer.Ordinal));

        /// <summary>
        /// Renders one row: <c>(T0 1 "a")</c>, or <c>(F0 1) = 3</c>.
        /// </summary>
        /// <param name="table">The table's name.</param>
        /// <param name="keys">The decoded keys.</param>
        /// <param name="output">The decoded output; <c>null</c> for a relation.</param>
        /// <returns>The rendering.</returns>
        public static string Row(string table, IEnumerable<object> keys, object? output)
        {
            var row = "(" + table + string.Concat(keys.Select(k => " " + Format(k))) + ")";
            return output is null ? row : row + " = " + Format(output);
        }

        /// <summary>
        /// Renders a value.
        /// </summary>
        /// <param name="value">A <c>long</c> or a <c>string</c>.</param>
        /// <returns>The rendering.</returns>
        public static string Format(object value) => value is string text ? "\"" + text + "\"" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        /// <summary>
        /// Builds a rule's body and head.
        /// </summary>
        void Build(RuleBuilder b, ScopedRuleSpec rule)
        {
            var variables = new Dictionary<string, Term>(StringComparer.Ordinal);
            foreach (var name in rule.Variables)
                variables[name] = name[0] == 'n' ? b.Var<Node>(name) : b.Var(name[0] == 's' ? types[1] : types[0], name);

            foreach (var atom in rule.Body)
                b.When(Atom(atom, variables, atom.Tag));

            if (rule.FilterVar is { } filtered)
                b.When(new FilterFact(Compute.Of(Primitives.Less, variables[filtered], Term.Constant(types[0], rule.FilterBelow))));

            // Every body variable is used, so match counts are distinct full bindings (§8.2, as colors' ColorSchema.Keep):
            // a value through a filter that keeps everything, a class through a disequality with a class no row holds.
            foreach (var name in rule.Variables)
            {
                if (name[0] == 'n')
                    b.When(new NotEqualFact(variables[name], (Term<Node>)keep));
                else
                    b.When(new FilterFact(Compute.Of(name[0] == 's' ? KeepString : KeepLong, variables[name])));
            }

            foreach (var atom in rule.Head)
                b.Then(Atom(atom, variables, -1));
        }

        /// <summary>
        /// Builds a query's body.
        /// </summary>
        void BuildQuery(QueryBuilder q, ScopedRuleSpec query)
        {
            var variables = new Dictionary<string, Term>(StringComparer.Ordinal);
            foreach (var name in query.Variables)
                variables[name] = q.Var(name[0] == 's' ? types[1] : types[0], name);

            foreach (var atom in query.Body)
                q.When(Atom(atom, variables, atom.Tag));
        }

        /// <summary>
        /// Builds an atom's fact: a relation row, or a function's or constructor's value equated with its output.
        /// </summary>
        Fact Atom(ScopedAtomSpec atom, Dictionary<string, Term>? variables, int tag)
        {
            var keys = atom.Keys.Select(k => TermOf(k, variables)).ToArray();
            var spec = program.Tables[atom.Table];
            switch (spec.Kind)
            {
                case ScopedTableKind.Relation:
                case ScopedTableKind.NodeRelation:
                {
                    var relation = (RelationHandle)tables[atom.Table];
                    return (tag >= 0 ? relation.InScope(tags[tag]) : relation).Apply(keys);
                }
                case ScopedTableKind.Function:
                {
                    var function = (FunctionHandle)tables[atom.Table];
                    return Fact.Equal((tag >= 0 ? function.InScope(tags[tag]) : function).Apply(keys), TermOf(atom.Output!, variables));
                }
                default:
                    return Fact.Equal(new ConstructorTerm<Node>((ConstructorHandle)tables[atom.Table], keys), TermOf(atom.Output!, variables));
            }
        }

        /// <summary>
        /// Builds a term.
        /// </summary>
        Term TermOf(ScopedTermSpec term, Dictionary<string, Term>? variables)
        {
            if (term.Var is { } name)
                return variables![name];

            if (term.Construct >= 0)
                return new ConstructorTerm<Node>((ConstructorHandle)tables[term.Construct], [TermOf(term.Argument!, variables)]);

            return Eggnog.Term.Constant(term.Constant is string ? types[1] : types[0], term.Constant!);
        }

    }

}
