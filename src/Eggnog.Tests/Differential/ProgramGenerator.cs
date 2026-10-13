using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// Generates a <see cref="RandomProgram"/> from a seed: 2–4 relations over <c>i64</c> and <c>String</c>; 1–2
    /// constructors of the sort <c>Node</c> with value and <c>Node</c> keys; 1–2 functions merged by
    /// <c>ValueLattice.Max&lt;long&gt;()</c> or <c>ValueLattice.Intersect&lt;int&gt;()</c>, and one without a lattice,
    /// written only by host writes with one value per key; 5–12 rules (joins of 1–4 atoms, named primitives and lambdas
    /// as filters and lets, lets checked against constants, rewrites, unions, <c>Make</c>, class constants from host
    /// <c>Make</c>s, naive rules); and schedules built of <c>Saturate</c>, <c>Repeat</c>, <c>Sequence</c> and
    /// <c>Until</c>, with host writes and queries between runs. Queries are defined before the first command, and up to
    /// two more between runs, which may hold the host's classes.
    /// </summary>
    /// <remarks>
    /// Every rule it makes is valid (range-restricted, well-typed, with writes only in heads), and every body after its
    /// first atom joins on a variable, so matches stay few. Values come from small domains, so programs stay small.
    /// </remarks>
    internal sealed class ProgramGenerator
    {

        static readonly string[] Strings = { "a", "b", "c" };

        readonly Random random;
        readonly int seed;
        readonly List<DiffTable> tables = new();
        readonly List<DiffTable> relations = new();
        readonly List<DiffTable> constructors = new();
        readonly List<DiffTable> lattices = new();
        readonly HashSet<string> hostKeys = new(StringComparer.Ordinal);
        readonly List<DiffCommand> commands = new();
        DiffTable host = null!;
        int rulesets;
        int handles;
        int rules;

        // The state of the rule being generated. The compiler shares the output of applications with equal arguments
        // (functions are functional), so a body never repeats an application, and never equates two variables that are
        // keys of applications: two outputs pinned to different constants would never match, a definition error. A
        // computation checked against a constant (p(args).Is(c), a check) pins the register it shares with every equal
        // computation of the body, so it is used only when the body has no equal computation, and no later let repeats
        // it.
        List<(string Name, DiffType Type)> variables = new();
        List<string> headVariables = new();
        HashSet<string> applications = new(StringComparer.Ordinal);
        HashSet<string> keyVariables = new(StringComparer.Ordinal);
        HashSet<string> computations = new(StringComparer.Ordinal);
        HashSet<string> checkedComputations = new(StringComparer.Ordinal);
        int fresh;

        /// <summary>
        /// Initializes a generator for one seed.
        /// </summary>
        /// <param name="seed">The seed.</param>
        public ProgramGenerator(int seed)
        {
            this.seed = seed;
            random = new Random(seed);
        }

        /// <summary>
        /// Generates the program.
        /// </summary>
        /// <returns>The program.</returns>
        public RandomProgram Generate()
        {
            DefineTables();
            rulesets = random.Next(1, 4);
            var queries = new List<DiffRule>();
            var queryCount = random.Next(1, 4);
            for (var i = 0; i < queryCount; i++)
                queries.Add(GenerateQuery("q" + i.ToString(CultureInfo.InvariantCulture)));

            // Initial host writes: facts, terms, maybe a union.
            commands.Add(DiffCommand.Assume(HostFacts(random.Next(8, 21))));
            var makes = random.Next(2, 7);
            for (var i = 0; i < makes; i++)
                AddMake();

            if (handles >= 2 && random.Next(3) == 0)
                commands.Add(DiffCommand.Union(random.Next(handles), random.Next(handles)));

            var ruleCount = random.Next(5, 13);
            var early = Math.Max(1, ruleCount * 2 / 3);
            for (var i = 0; i < early; i++)
                commands.Add(DiffCommand.Define(GenerateRule()));

            // Queries defined after the first Make may hold the host's classes, and are asked after unions, which
            // change the canonical forms the engine runs those class constants in.
            var defined = queries.Count;
            var rounds = random.Next(2, 6);
            for (var round = 0; round < rounds; round++)
            {
                commands.Add(DiffCommand.Run(TopSchedule(), random.Next(4, 11)));
                if (defined - queries.Count < 2 && random.Next(3) == 0)
                    commands.Add(DiffCommand.DefineQuery(GenerateQuery("q" + (defined++).ToString(CultureInfo.InvariantCulture))));

                if (random.Next(2) == 0)
                    commands.Add(DiffCommand.Query(random.Next(defined), (DiffQueryMode)random.Next(3)));

                var writes = random.Next(0, 4);
                for (var i = 0; i < writes; i++)
                {
                    switch (random.Next(4))
                    {
                        case 0:
                            AddMake();
                            break;
                        case 1 when handles >= 2:
                            commands.Add(DiffCommand.Union(random.Next(handles), random.Next(handles)));
                            break;
                        default:
                            commands.Add(DiffCommand.Assume(HostFacts(random.Next(2, 7))));
                            break;
                    }
                }

                if (rules < ruleCount && random.Next(2) == 0)
                    commands.Add(DiffCommand.Define(GenerateRule()));
            }

            while (rules < ruleCount)
                commands.Add(DiffCommand.Define(GenerateRule()));

            commands.Add(DiffCommand.Run(TopSchedule(), random.Next(4, 11)));
            for (var i = 0; i < defined; i++)
                commands.Add(DiffCommand.Query(i, DiffQueryMode.ForEach));

            return new RandomProgram(seed, tables, rulesets, queries, commands);
        }

        /// <summary>
        /// Defines the tables, in an order that mixes kinds.
        /// </summary>
        void DefineTables()
        {
            var relationCount = random.Next(2, 5);
            for (var i = 0; i < relationCount; i++)
            {
                var arity = random.Next(1, 3);
                var keys = new DiffType[arity];
                for (var k = 0; k < arity; k++)
                    keys[k] = random.Next(2) == 0 ? DiffType.I64 : DiffType.Str;

                relations.Add(Add("r" + i.ToString(CultureInfo.InvariantCulture), TableKind.Relation, keys, null, DiffLattice.None));
            }

            // C0 is keyed by a value only, so ground terms can be built bottom up.
            constructors.Add(Add("C0", TableKind.Constructor, new[] { random.Next(2) == 0 ? DiffType.I64 : DiffType.Str }, DiffType.Node, DiffLattice.None));
            if (random.Next(3) > 0)
            {
                var keys = random.Next(3) switch
                {
                    0 => new[] { DiffType.Node },
                    1 => new[] { DiffType.Node, DiffType.Node },
                    _ => new[] { DiffType.Node, DiffType.I64 },
                };
                constructors.Add(Add("C1", TableKind.Constructor, keys, DiffType.Node, DiffLattice.None));
            }

            var functionCount = random.Next(1, 3);
            for (var i = 0; i < functionCount; i++)
            {
                var key = random.Next(3) switch
                {
                    0 => DiffType.I64,
                    1 => DiffType.Str,
                    _ => DiffType.Node,
                };
                var max = random.Next(2) == 0;
                lattices.Add(Add("f" + i.ToString(CultureInfo.InvariantCulture), TableKind.Function, new[] { key },
                    max ? DiffType.I64 : DiffType.IntSet, max ? DiffLattice.Max : DiffLattice.Intersect));
            }

            host = Add("h", TableKind.Function, new[] { random.Next(2) == 0 ? DiffType.I64 : DiffType.Str },
                random.Next(2) == 0 ? DiffType.I64 : DiffType.Str, DiffLattice.None);
        }

        /// <summary>
        /// Adds a table.
        /// </summary>
        DiffTable Add(string name, TableKind kind, DiffType[] keys, DiffType? output, DiffLattice lattice)
        {
            var table = new DiffTable(tables.Count, name, kind, keys, output, lattice);
            tables.Add(table);
            return table;
        }

        /// <summary>
        /// Adds a host <c>Make</c> of a ground term: a new handle.
        /// </summary>
        void AddMake()
        {
            commands.Add(DiffCommand.Make(GroundConstructor(0)));
            handles++;
        }

        /// <summary>
        /// Ground host facts: relation rows, function writes (the host-only function once per key), and equalities of
        /// ground terms of the sort.
        /// </summary>
        DiffFact[] HostFacts(int count)
        {
            var facts = new List<DiffFact>();
            for (var i = 0; i < count; i++)
            {
                switch (random.Next(6))
                {
                    case 0:
                    case 1:
                    {
                        var relation = Pick(relations);
                        facts.Add(DiffFact.Row(relation, relation.Keys.Select(GroundValue).ToArray()));
                        break;
                    }
                    case 2:
                    {
                        var function = Pick(lattices);
                        facts.Add(DiffFact.Equal(DiffTerm.App(function, Ground(function.Keys[0])), GroundValue(function.Output!.Value)));
                        break;
                    }
                    case 3:
                    {
                        var key = GroundValue(host.Keys[0]);
                        if (hostKeys.Add(Key(key)))
                            facts.Add(DiffFact.Equal(DiffTerm.App(host, key), GroundValue(host.Output!.Value)));

                        break;
                    }
                    default:
                        facts.Add(DiffFact.Equal(Ground(DiffType.Node), Ground(DiffType.Node)));
                        break;
                }
            }

            return facts.ToArray();
        }

        /// <summary>
        /// A ground term of a type: a value constant, or for the sort a handle or a ground constructor term.
        /// </summary>
        DiffTerm Ground(DiffType type)
        {
            if (type != DiffType.Node)
                return GroundValue(type);

            if (handles > 0 && random.Next(2) == 0)
                return DiffTerm.Handle(random.Next(handles));

            return GroundConstructor(0);
        }

        /// <summary>
        /// A ground constructor term, at most two deep.
        /// </summary>
        DiffTerm GroundConstructor(int depth)
        {
            var constructor = depth < 1 && constructors.Count > 1 && random.Next(2) == 0 ? constructors[1] : constructors[0];
            var arguments = new DiffTerm[constructor.Keys.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                var type = constructor.Keys[i];
                arguments[i] = type == DiffType.Node
                    ? handles > 0 && random.Next(2) == 0 ? DiffTerm.Handle(random.Next(handles)) : GroundConstructor(depth + 1)
                    : GroundValue(type);
            }

            return DiffTerm.App(constructor, arguments);
        }

        /// <summary>
        /// A value constant from the type's small domain.
        /// </summary>
        DiffTerm GroundValue(DiffType type) => type switch
        {
            DiffType.I64 => DiffTerm.Const(DiffType.I64, (long)random.Next(0, 6)),
            DiffType.Str => DiffTerm.Const(DiffType.Str, Strings[random.Next(Strings.Length)]),
            DiffType.IntSet => DiffTerm.Const(DiffType.IntSet, RandomSet()),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a value type."),
        };

        /// <summary>
        /// A subset of {0, …, 4} with at most three elements.
        /// </summary>
        ValueSet<int> RandomSet()
        {
            var size = random.Next(0, 4);
            var elements = new List<int>();
            for (var i = 0; i < size; i++)
                elements.Add(random.Next(0, 5));

            return ValueSet.From(elements);
        }

        /// <summary>
        /// A string key of a constant, for the host-only function's one write per key.
        /// </summary>
        static string Key(DiffTerm constant) => Convert.ToString(constant.Value, CultureInfo.InvariantCulture)!;

        /// <summary>
        /// The schedule of a run: often a saturation.
        /// </summary>
        DiffSchedule TopSchedule() => random.Next(2) == 0 ? DiffSchedule.Of(DiffScheduleKind.Saturate, 0, Schedule(1)) : Schedule(0);

        /// <summary>
        /// A schedule node, at most three deep.
        /// </summary>
        DiffSchedule Schedule(int depth)
        {
            var choice = random.Next(10);
            if (depth >= 2 || choice < 4)
            {
                var chosen = Enumerable.Range(0, rulesets).Where(_ => random.Next(2) == 0).ToArray();
                return DiffSchedule.Run(chosen.Length > 0 ? chosen : new[] { random.Next(rulesets) });
            }

            if (choice < 6)
                return DiffSchedule.Of(DiffScheduleKind.Saturate, 0, Steps(depth, 2));

            if (choice < 7)
                return DiffSchedule.Of(DiffScheduleKind.Repeat, random.Next(1, 4), Steps(depth, 2));

            if (choice < 9)
                return DiffSchedule.Of(DiffScheduleKind.Sequence, 0, Steps(depth, 3));

            var condition = new DiffFact[random.Next(1, 3)];
            for (var i = 0; i < condition.Length; i++)
                condition[i] = CheckFact();

            return DiffSchedule.Until(Schedule(depth + 1), condition);
        }

        /// <summary>
        /// One to <paramref name="most"/> steps of a composite node.
        /// </summary>
        DiffSchedule[] Steps(int depth, int most)
        {
            var steps = new DiffSchedule[random.Next(1, most + 1)];
            for (var i = 0; i < steps.Length; i++)
                steps[i] = Schedule(depth + 1);

            return steps;
        }

        /// <summary>
        /// A ground fact to check in an until: a relation row, a function value, an existing term, a term's class.
        /// </summary>
        DiffFact CheckFact()
        {
            switch (random.Next(4))
            {
                case 0:
                {
                    var relation = Pick(relations);
                    return DiffFact.Row(relation, relation.Keys.Select(GroundValue).ToArray());
                }
                case 1:
                {
                    var function = Pick(lattices);
                    return DiffFact.Equal(DiffTerm.App(function, Ground(function.Keys[0])), GroundValue(function.Output!.Value));
                }
                case 2:
                    return DiffFact.Exists(GroundConstructor(0));
                default:
                    return handles > 0 ? DiffFact.Equal(DiffTerm.Handle(random.Next(handles)), GroundConstructor(0)) : DiffFact.Exists(GroundConstructor(0));
            }
        }

        /// <summary>
        /// A query: a body of one to three atoms and at most one step; its variables are every variable of the body.
        /// </summary>
        DiffRule GenerateQuery(string name)
        {
            StartRule();
            var body = new List<DiffFact>();
            Body(body, random.Next(1, 4), random.Next(0, 2));
            return new DiffRule(name, -1, false, variables, body, new List<DiffHead>());
        }

        /// <summary>
        /// A rule: a rewrite, or a body of one to four atoms and up to two steps with a head of one to three items.
        /// </summary>
        DiffRule GenerateRule()
        {
            var name = "rule" + rules.ToString(CultureInfo.InvariantCulture);
            rules++;
            StartRule();
            var body = new List<DiffFact>();
            var head = new List<DiffHead>();
            var naive = random.Next(5) == 0;
            var ruleset = random.Next(rulesets);
            if (random.Next(4) == 0)
            {
                Rewrite(body, head);
                return new DiffRule(name, ruleset, naive, variables, body, head);
            }

            Body(body, random.Next(1, 5), random.Next(0, 3));
            var items = random.Next(1, 4);
            for (var i = 0; i < items; i++)
                HeadItem(head);

            return new DiffRule(name, ruleset, naive, variables, body, head);
        }

        /// <summary>
        /// Starts a rule or query: no variables yet.
        /// </summary>
        void StartRule()
        {
            variables = new List<(string, DiffType)>();
            headVariables = new List<string>();
            applications = new HashSet<string>(StringComparer.Ordinal);
            keyVariables = new HashSet<string>(StringComparer.Ordinal);
            computations = new HashSet<string>(StringComparer.Ordinal);
            checkedComputations = new HashSet<string>(StringComparer.Ordinal);
            fresh = 0;
        }

        /// <summary>
        /// Declares a fresh body variable.
        /// </summary>
        DiffTerm Fresh(DiffType type)
        {
            var name = "x" + (fresh++).ToString(CultureInfo.InvariantCulture);
            variables.Add((name, type));
            return DiffTerm.Var(name, type);
        }

        /// <summary>
        /// The bound body variables of a type.
        /// </summary>
        List<DiffTerm> Bound(DiffType type) => variables.Where(v => v.Type == type).Select(v => DiffTerm.Var(v.Name, v.Type)).ToList();

        /// <summary>
        /// Generates body atoms, each after the first joining on a variable, then steps over the bound variables.
        /// </summary>
        void Body(List<DiffFact> body, int atoms, int steps)
        {
            for (var a = 0; a < atoms; a++)
            {
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    var table = random.Next(10) switch
                    {
                        < 4 => Pick(relations),
                        < 7 => Pick(constructors),
                        < 9 => Pick(lattices),
                        _ => host,
                    };

                    var columns = table.Kind == TableKind.Relation ? table.Keys : table.Keys.Append(table.Output!.Value).ToArray();
                    var joinable = Enumerable.Range(0, columns.Length).Where(i => Bound(columns[i]).Count > 0).ToList();
                    if (a > 0 && joinable.Count == 0)
                        continue;

                    var join = joinable.Count > 0 ? joinable[random.Next(joinable.Count)] : -1;

                    // An existence of a constructor term has no output column, so it never joins on one.
                    var exists = table.Kind == TableKind.Constructor && join != columns.Length - 1 && random.Next(5) == 0;
                    var keys = new DiffTerm[table.Keys.Length];
                    for (var i = 0; i < keys.Length; i++)
                        keys[i] = i == join ? Pick(Bound(columns[i])) : Argument(columns[i], true);

                    if (table.Kind == TableKind.Relation)
                    {
                        body.Add(DiffFact.Row(table, keys));
                        break;
                    }

                    var application = DiffTerm.App(table, keys);
                    if (Applied(application) == false)
                        break;

                    if (exists)
                    {
                        body.Add(DiffFact.Exists(application));
                        break;
                    }

                    var output = join == columns.Length - 1 ? Pick(Bound(columns[^1])) : Argument(columns[^1], false);
                    body.Add(random.Next(2) == 0 ? DiffFact.Equal(output, application) : DiffFact.Equal(application, output));
                    break;
                }
            }

            for (var s = 0; s < steps; s++)
                Step(body);
        }

        /// <summary>
        /// Records a body application and the variables among its keys; <c>false</c> when the body has it already.
        /// </summary>
        bool Applied(DiffTerm application)
        {
            foreach (var argument in application.Arguments)
                if (argument.Kind == DiffTermKind.Var)
                    keyVariables.Add(argument.Name!);

            return applications.Add(ProgramPrinter.Term(application));
        }

        /// <summary>
        /// An argument of a body atom: a bound variable, a constant, a nested constructor pattern, or a fresh variable.
        /// </summary>
        DiffTerm Argument(DiffType type, bool key)
        {
            var bound = Bound(type);
            var choice = random.Next(20);
            if (bound.Count > 0 && choice < 8)
                return Pick(bound);

            if (choice < 10)
            {
                if (type != DiffType.Node)
                    return GroundValue(type);

                if (handles > 0)
                    return DiffTerm.Handle(random.Next(handles));
            }

            if (type == DiffType.Node && key && choice < 13)
            {
                var nested = DiffTerm.App(constructors[0], Argument(constructors[0].Keys[0], true));
                Applied(nested);
                return nested;
            }

            return Fresh(type);
        }

        /// <summary>
        /// A body step over bound variables: a filter (named or lambda), a let (named or lambda) of a fresh variable, a
        /// check against a bound variable or a constant, a disequality or a join of two variables.
        /// </summary>
        void Step(List<DiffFact> body)
        {
            var numbers = Bound(DiffType.I64);
            var strings = Bound(DiffType.Str);
            var sets = Bound(DiffType.IntSet);
            switch (random.Next(10))
            {
                case 0 when numbers.Count > 0:
                {
                    var comparison = Pick(new[] { DiffPrimitive.Less, DiffPrimitive.Greater, DiffPrimitive.LessOrEqual, DiffPrimitive.GreaterOrEqual });
                    var other = numbers.Count > 1 && random.Next(2) == 0 ? Pick(numbers) : GroundValue(DiffType.I64);
                    body.Add(DiffFact.Filter(DiffTerm.Prim(comparison, Pick(numbers), other)));
                    break;
                }
                case 1 when numbers.Count > 0:
                    body.Add(DiffFact.Filter(DiffTerm.Prim(DiffPrimitive.Modulo(random.Next(2, 4), random.Next(0, 2)), Pick(numbers))));
                    break;
                case 2 when strings.Count > 0:
                    body.Add(DiffFact.Filter(DiffTerm.Prim(DiffPrimitive.StringLow, Pick(strings))));
                    break;
                case 3 when sets.Count > 0:
                    body.Add(DiffFact.Filter(DiffTerm.Prim(random.Next(2) == 0 ? DiffPrimitive.SetBig : DiffPrimitive.SetEmpty, Pick(sets))));
                    break;
                case 4 when numbers.Count > 0:
                {
                    var computed = random.Next(4) switch
                    {
                        0 => DiffTerm.Prim(DiffPrimitive.Add, Pick(numbers), GroundValue(DiffType.I64)),
                        1 => DiffTerm.Prim(DiffPrimitive.Affine, Pick(numbers)),
                        2 => DiffTerm.Prim(DiffPrimitive.SumMod, Pick(numbers), Pick(numbers)),
                        _ => DiffTerm.Prim(DiffPrimitive.Max, Pick(numbers), GroundValue(DiffType.I64)),
                    };

                    if (Computed(computed) == false)
                        break;

                    // A let of a fresh variable, or a check against a bound one.
                    var target = numbers.Count > 1 && random.Next(3) == 0 ? Pick(numbers) : Fresh(DiffType.I64);
                    body.Add(random.Next(2) == 0 ? DiffFact.Equal(target, computed) : DiffFact.Equal(computed, target));
                    break;
                }
                case 5 when sets.Count > 0:
                {
                    var computed = DiffTerm.Prim(DiffPrimitive.SetLength, Pick(sets));
                    if (Computed(computed))
                        body.Add(DiffFact.Equal(Fresh(DiffType.I64), computed));

                    break;
                }
                case 6 when strings.Count > 0:
                {
                    var computed = DiffTerm.Prim(DiffPrimitive.StringGrow, Pick(strings));
                    if (Computed(computed))
                        body.Add(DiffFact.Equal(Fresh(DiffType.Str), computed));

                    break;
                }
                case 9 when numbers.Count > 0 || sets.Count > 0 || strings.Count > 0:
                {
                    // A let checked against a constant: p(args).Is(c), either side.
                    var kind = random.Next(3);
                    DiffTerm computed;
                    DiffTerm constant;
                    if (kind == 0 && sets.Count > 0)
                    {
                        computed = DiffTerm.Prim(DiffPrimitive.SetLength, Pick(sets));
                        constant = GroundValue(DiffType.I64);
                    }
                    else if (kind == 1 && strings.Count > 0)
                    {
                        computed = DiffTerm.Prim(DiffPrimitive.StringGrow, Pick(strings));
                        constant = DiffTerm.Const(DiffType.Str, Pick(Strings) + "x");
                    }
                    else if (numbers.Count > 0)
                    {
                        computed = random.Next(4) switch
                        {
                            0 => DiffTerm.Prim(DiffPrimitive.Add, Pick(numbers), GroundValue(DiffType.I64)),
                            1 => DiffTerm.Prim(DiffPrimitive.Affine, Pick(numbers)),
                            2 => DiffTerm.Prim(DiffPrimitive.SumMod, Pick(numbers), Pick(numbers)),
                            _ => DiffTerm.Prim(DiffPrimitive.Max, Pick(numbers), GroundValue(DiffType.I64)),
                        };
                        constant = GroundValue(DiffType.I64);
                    }
                    else
                    {
                        break;
                    }

                    var text = ProgramPrinter.Term(computed);
                    if (computations.Add(text) == false)
                        break;

                    checkedComputations.Add(text);
                    body.Add(random.Next(2) == 0 ? DiffFact.Equal(computed, constant) : DiffFact.Equal(constant, computed));
                    break;
                }
                case 7:
                {
                    var type = Pick(new[] { DiffType.I64, DiffType.Str, DiffType.Node });
                    var bound = Bound(type);
                    if (bound.Count > 0)
                        body.Add(DiffFact.NotEqual(Pick(bound), bound.Count > 1 ? Pick(bound) : Ground(type)));

                    break;
                }
                case 8:
                {
                    var type = Pick(new[] { DiffType.I64, DiffType.Str, DiffType.Node });
                    var bound = Bound(type).Where(v => keyVariables.Contains(v.Name!) == false).ToList();
                    if (bound.Count > 1)
                        body.Add(DiffFact.Equal(Pick(bound), Pick(bound)));

                    break;
                }
            }
        }

        /// <summary>
        /// Records a let's computation; <c>false</c> when the body checks an equal computation against a constant,
        /// whose register a let would share.
        /// </summary>
        bool Computed(DiffTerm computation)
        {
            var text = ProgramPrinter.Term(computation);
            if (checkedComputations.Contains(text))
                return false;

            computations.Add(text);
            return true;
        }

        /// <summary>
        /// A head item over the body's variables: a relation row, a lattice function write, a <c>Make</c>, a union, or
        /// a constructor write.
        /// </summary>
        void HeadItem(List<DiffHead> head)
        {
            switch (random.Next(6))
            {
                case 0:
                case 1:
                {
                    var relation = Pick(relations);
                    head.Add(DiffHead.Then(DiffFact.Row(relation, relation.Keys.Select(HeadValue).ToArray())));
                    break;
                }
                case 2:
                {
                    var function = Pick(lattices);
                    var key = function.Keys[0] == DiffType.Node ? HeadNode(false) : HeadValue(function.Keys[0]);
                    var value = HeadValue(function.Output!.Value);
                    head.Add(DiffHead.Then(random.Next(2) == 0 ? DiffFact.Equal(DiffTerm.App(function, key), value) : DiffFact.Equal(value, DiffTerm.App(function, key))));
                    break;
                }
                case 3:
                {
                    var name = "m" + headVariables.Count.ToString(CultureInfo.InvariantCulture);
                    head.Add(DiffHead.Make(name, HeadConstructor()));
                    headVariables.Add(name);
                    break;
                }
                case 4:
                    head.Add(DiffHead.Then(DiffFact.Equal(HeadNode(true), HeadNode(true))));
                    break;
                default:
                {
                    var application = HeadConstructor();
                    var output = HeadNode(false);
                    head.Add(DiffHead.Then(random.Next(2) == 0 ? DiffFact.Equal(application, output) : DiffFact.Equal(output, application)));
                    break;
                }
            }
        }

        /// <summary>
        /// A head term of a value type: a bound variable, a constant, or a computation over bound variables.
        /// </summary>
        DiffTerm HeadValue(DiffType type)
        {
            var bound = Bound(type);
            var choice = random.Next(10);
            if (bound.Count > 0 && choice < 5)
                return Pick(bound);

            if (choice < 8)
            {
                switch (type)
                {
                    case DiffType.I64 when bound.Count > 0:
                        return random.Next(2) == 0 ? DiffTerm.Prim(DiffPrimitive.Add, Pick(bound), DiffTerm.Const(DiffType.I64, 1L)) : DiffTerm.Prim(DiffPrimitive.Affine, Pick(bound));
                    case DiffType.Str when bound.Count > 0:
                        return DiffTerm.Prim(DiffPrimitive.StringGrow, Pick(bound));
                    case DiffType.IntSet when bound.Count > 0:
                        return random.Next(2) == 0 ? DiffTerm.Prim(DiffPrimitive.SetEven, Pick(bound)) : DiffTerm.Prim(DiffPrimitive.SetUnion, Pick(bound), GroundValue(DiffType.IntSet));
                    case DiffType.IntSet when Bound(DiffType.I64).Count > 0:
                        return DiffTerm.Prim(DiffPrimitive.SetAround, Pick(Bound(DiffType.I64)));
                }
            }

            return GroundValue(type);
        }

        /// <summary>
        /// A head term of the sort: a bound variable, an earlier <c>Make</c> variable, a handle, or (when
        /// <paramref name="applications"/>) a get-or-add of a constructor term.
        /// </summary>
        DiffTerm HeadNode(bool applications)
        {
            var bound = Bound(DiffType.Node);
            var choice = random.Next(10);
            if (bound.Count > 0 && choice < 4)
                return Pick(bound);

            if (headVariables.Count > 0 && choice < 6)
                return DiffTerm.Var(Pick(headVariables), DiffType.Node);

            if (handles > 0 && choice < 7)
                return DiffTerm.Handle(random.Next(handles));

            if (applications || (bound.Count == 0 && headVariables.Count == 0 && handles == 0))
                return HeadConstructor();

            return bound.Count > 0 ? Pick(bound) : headVariables.Count > 0 ? DiffTerm.Var(Pick(headVariables), DiffType.Node) : DiffTerm.Handle(random.Next(handles));
        }

        /// <summary>
        /// A head constructor term over the body's variables, earlier <c>Make</c> variables and handles; a <c>C1</c>
        /// term takes at most one variable of the body, so terms grow at most linearly per iteration.
        /// </summary>
        DiffTerm HeadConstructor()
        {
            if (constructors.Count == 1 || random.Next(2) == 0)
                return DiffTerm.App(constructors[0], HeadValue(constructors[0].Keys[0]));

            var constructor = constructors[1];
            var arguments = new DiffTerm[constructor.Keys.Length];
            var usedBody = false;
            for (var i = 0; i < arguments.Length; i++)
            {
                if (constructor.Keys[i] != DiffType.Node)
                {
                    arguments[i] = HeadValue(constructor.Keys[i]);
                    continue;
                }

                var bound = Bound(DiffType.Node);
                if (usedBody == false && bound.Count > 0 && random.Next(2) == 0)
                {
                    arguments[i] = Pick(bound);
                    usedBody = true;
                }
                else if (headVariables.Count > 0 && random.Next(2) == 0)
                {
                    arguments[i] = DiffTerm.Var(Pick(headVariables), DiffType.Node);
                }
                else if (handles > 0)
                {
                    arguments[i] = DiffTerm.Handle(random.Next(handles));
                }
                else
                {
                    arguments[i] = DiffTerm.App(constructors[0], GroundValue(constructors[0].Keys[0]));
                }
            }

            return DiffTerm.App(constructor, arguments);
        }

        /// <summary>
        /// A rewrite: a constructor pattern over fresh variables, rewritten to one of its class variables, a
        /// constructor term over its variables, or a handle; sometimes guarded by an extra atom.
        /// </summary>
        void Rewrite(List<DiffFact> body, List<DiffHead> head)
        {
            var constructor = Pick(constructors);
            var arguments = new DiffTerm[constructor.Keys.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                var type = constructor.Keys[i];
                arguments[i] = type == DiffType.Node && random.Next(3) == 0
                    ? DiffTerm.App(constructors[0], Fresh(constructors[0].Keys[0]))
                    : random.Next(6) == 0 && type != DiffType.Node ? GroundValue(type) : Fresh(type);
            }

            var left = DiffTerm.App(constructor, arguments);
            if (random.Next(3) == 0)
            {
                var values = variables.Where(v => v.Type != DiffType.Node).ToList();
                if (values.Count > 0)
                {
                    var value = Pick(values);
                    var relation = relations.FirstOrDefault(r => r.Keys.Contains(value.Type));
                    if (relation is not null)
                        body.Add(DiffFact.Row(relation, relation.Keys.Select(k => k == value.Type ? DiffTerm.Var(value.Name, value.Type) : Fresh(k)).ToArray()));
                }
            }

            var nodes = Bound(DiffType.Node);
            DiffTerm right;
            var choice = random.Next(10);
            if (nodes.Count > 0 && choice < 4)
            {
                right = Pick(nodes);
            }
            else if (choice < 8)
            {
                // Permute the pattern's own arguments within a constructor of the same keys, or wrap a value.
                if (constructor.Keys.Length == 2 && constructor.Keys[0] == constructor.Keys[1])
                    right = DiffTerm.App(constructor, arguments[1], arguments[0]);
                else
                    right = DiffTerm.App(constructors[0], HeadValue(constructors[0].Keys[0]));
            }
            else if (handles > 0)
            {
                right = DiffTerm.Handle(random.Next(handles));
            }
            else
            {
                right = DiffTerm.App(constructors[0], GroundValue(constructors[0].Keys[0]));
            }

            head.Add(DiffHead.Rewrite(left, right));
        }

        /// <summary>
        /// Picks an item.
        /// </summary>
        T Pick<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];

    }

}
