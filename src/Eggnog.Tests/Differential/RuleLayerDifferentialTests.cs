using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// The rule layer's differential test: random programs run three ways, Eggnog semi-naive, Eggnog with every rule's
    /// naive twin (the internal <c>RunOptions.SemiNaive = false</c>), and the <see cref="ReferenceInterpreter"/>, and
    /// every command's observation must agree: after every run its report (iterations, stop reason, saturation, change)
    /// and every table's rows (<see cref="Scope.Rows(TableHandle)"/>, values decoded and classes named by
    /// <see cref="TermNamer"/>), after every host write the rows, and every query's answer. 300 seeds by default;
    /// <c>EGGNOG_SEEDS</c> raises it. A failing program prints itself as C# source.
    /// </summary>
    /// <remarks>
    /// A deeper check sets <c>EGGNOG_SEEDS=5000</c>; with this generator (lets checked against constants, and queries
    /// defined after the host's writes) it runs in about 39 s on net10.0 and on net8.0.
    /// </remarks>
    public sealed class RuleLayerDifferentialTests
    {

        /// <summary>
        /// The number of seeds without <c>EGGNOG_SEEDS</c>.
        /// </summary>
        const int DefaultSeeds = 300;

        /// <summary>
        /// A program whose tables hold more rows than this after a run stops there: what it did so far was compared.
        /// </summary>
        const long RowCap = 2500;

        /// <summary>
        /// Every seed's program observes the same in all three runs: semi-naive, naive and the reference. At most three
        /// failing programs are reported.
        /// </summary>
        [Fact]
        public void EggnogAgreesWithItsNaiveTwinAndTheReference()
        {
            var seeds = Seeds();
            var failures = new List<string>();
            for (var seed = 0; seed < seeds && failures.Count < 3; seed++)
                if (Run(seed) is { } failure)
                    failures.Add(failure);

            Assert.True(failures.Count == 0, string.Join("\n\n", failures));
        }

        /// <summary>
        /// The generator produces every shape the comparison relies on: two to four relations, one or two constructors
        /// and lattice functions, one host-only table and five to twelve rules per program; naive rules; rewrite, make
        /// and class-union heads; bodies of four atoms or more; lambda and non-lambda primitives, as terms and as
        /// filters; disequalities and class constants in bodies; saturate, repeat, sequence and until schedules; host
        /// unions and queries in every program; lets checked against constants in rules and queries; queries defined
        /// after the host made classes; and a query asked after a union.
        /// </summary>
        [Fact]
        public void TheGeneratedProgramsCoverEveryShapeUnderTest()
        {
            var programs = Enumerable.Range(0, DefaultSeeds).Select(RandomProgram.Generate).ToList();
            var rules = programs.SelectMany(p => p.Commands.Where(c => c.Kind == DiffCommandKind.DefineRule).Select(c => c.Rule!)).ToList();
            var schedules = programs.SelectMany(p => p.Commands.Where(c => c.Kind == DiffCommandKind.Run).Select(c => c.Schedule!)).SelectMany(Nodes).ToList();
            var bodyTerms = rules.SelectMany(r => r.Body).SelectMany(Terms).ToList();

            Assert.All(programs, p => Assert.InRange(p.Tables.Count(t => t.Kind == TableKind.Relation), 2, 4));
            Assert.All(programs, p => Assert.InRange(p.Tables.Count(t => t.Kind == TableKind.Constructor), 1, 2));
            Assert.All(programs, p => Assert.InRange(p.Tables.Count(t => t.Kind == TableKind.Function && t.Lattice != DiffLattice.None), 1, 2));
            Assert.All(programs, p => Assert.Single(p.Tables, t => t.HostOnly));
            Assert.All(programs, p => Assert.InRange(p.Commands.Count(c => c.Kind == DiffCommandKind.DefineRule), 5, 12));
            Assert.Contains(rules, r => r.Naive);
            Assert.Contains(rules, r => r.Head.Any(h => h.Kind == DiffHeadKind.Rewrite));
            Assert.Contains(rules, r => r.Head.Any(h => h.Kind == DiffHeadKind.Make));
            Assert.Contains(rules, r => r.Head.Any(h => h.Kind == DiffHeadKind.Then && h.Fact!.Kind == DiffFactKind.Equal && h.Fact.Left!.Type == DiffType.Node));
            Assert.Contains(rules, r => r.Body.Count(f => f.Kind is DiffFactKind.Relation or DiffFactKind.Equal) >= 4);
            Assert.Contains(bodyTerms, t => t.Kind == DiffTermKind.Prim && t.Primitive!.IsLambda);
            Assert.Contains(bodyTerms, t => t.Kind == DiffTermKind.Prim && t.Primitive!.IsLambda == false);
            Assert.Contains(rules, r => r.Body.Any(f => f.Kind == DiffFactKind.Filter && f.Left!.Primitive!.IsLambda));
            Assert.Contains(rules, r => r.Body.Any(f => f.Kind == DiffFactKind.Filter && f.Left!.Primitive!.IsLambda == false));
            Assert.Contains(rules, r => r.Body.Any(f => f.Kind == DiffFactKind.NotEqual));
            Assert.Contains(bodyTerms, t => t.Kind == DiffTermKind.Const && t.Type == DiffType.Node);
            Assert.Contains(schedules, s => s.Kind == DiffScheduleKind.Saturate);
            Assert.Contains(schedules, s => s.Kind == DiffScheduleKind.Repeat);
            Assert.Contains(schedules, s => s.Kind == DiffScheduleKind.Sequence);
            Assert.Contains(schedules, s => s.Kind == DiffScheduleKind.Until);
            Assert.Contains(programs, p => p.Commands.Any(c => c.Kind == DiffCommandKind.Union));
            Assert.All(programs, p => Assert.Contains(p.Commands, c => c.Kind == DiffCommandKind.Query));

            // Lets checked against constants (p(args).Is(c)), in rules and in queries; and queries defined after the
            // host made classes, holding some, asked after a union.
            var lateQueries = programs.SelectMany(p => p.Commands.Where(c => c.Kind == DiffCommandKind.DefineQuery).Select(c => c.Rule!)).ToList();
            var queries = programs.SelectMany(p => p.Queries).Concat(lateQueries).ToList();
            Assert.Contains(rules, r => r.Body.Any(IsConstantCheck));
            Assert.Contains(queries, q => q.Body.Any(IsConstantCheck));
            Assert.Contains(lateQueries, q => q.Body.SelectMany(Terms).Any(t => t.Kind == DiffTermKind.Const && t.Type == DiffType.Node));
            Assert.Contains(programs, p => AskedAfterAUnion(p));
        }

        /// <summary>
        /// Whether a body fact is a computation checked against a constant.
        /// </summary>
        static bool IsConstantCheck(DiffFact fact) => fact.Kind == DiffFactKind.Equal
            && ((fact.Left!.Kind == DiffTermKind.Prim && fact.Right!.Kind == DiffTermKind.Const)
                || (fact.Right!.Kind == DiffTermKind.Prim && fact.Left!.Kind == DiffTermKind.Const));

        /// <summary>
        /// Whether a program asks a late query holding a class after a union that follows the query's definition.
        /// </summary>
        static bool AskedAfterAUnion(RandomProgram program)
        {
            var number = program.Queries.Count;
            var holding = new HashSet<int>();
            var unioned = new HashSet<int>();
            foreach (var command in program.Commands)
            {
                switch (command.Kind)
                {
                    case DiffCommandKind.DefineQuery:
                        if (command.Rule!.Body.SelectMany(Terms).Any(t => t.Kind == DiffTermKind.Const && t.Type == DiffType.Node))
                            holding.Add(number);

                        number++;
                        break;
                    case DiffCommandKind.Union:
                        unioned.UnionWith(holding);
                        break;
                    case DiffCommandKind.Query when unioned.Contains(command.First):
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A program prints itself as C# source, starting with its seed, and the same seed prints the same source.
        /// </summary>
        [Fact]
        public void AProgramPrintsItselfAsCSharp()
        {
            var program = RandomProgram.Generate(7);
            var source = program.ToString();
            Assert.StartsWith("// Random program, seed 7\nvar graph = new EGraph();\n", source);
            Assert.Contains(".DefineRule(\"rule0\", r =>", source);
            Assert.Contains("root.Run(Schedule.", source);
            Assert.Equal(source, RandomProgram.Generate(7).ToString());
        }

        /// <summary>
        /// Runs one seed's program three ways; the failure's description, or <c>null</c>.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <returns>The failure, or <c>null</c>.</returns>
        internal static string? Run(int seed)
        {
            var program = RandomProgram.Generate(seed);
            var step = -1;
            try
            {
                var semiNaive = new EggnogDriver(program, true);
                var naive = new EggnogDriver(program, false);
                var reference = new ReferenceInterpreter(program);
                for (step = 0; step < program.Commands.Count; step++)
                {
                    var command = program.Commands[step];
                    var expected = reference.Execute(command);
                    var actual = semiNaive.Execute(command);
                    var twin = naive.Execute(command);
                    if (actual != expected)
                        return Describe(program, step, "semi-naive", expected, actual);

                    if (twin != expected)
                        return Describe(program, step, "naive", expected, twin);

                    if (command.Kind == DiffCommandKind.Run && reference.TotalRows > RowCap)
                        break;
                }

                return null;
            }
            catch (Exception exception)
            {
                return $"Seed {seed} threw at command {step}: {exception}\n{program}";
            }
        }

        /// <summary>
        /// Describes a difference: the seed, the command, the first differing line, both observations and the program.
        /// </summary>
        static string Describe(RandomProgram program, int step, string mode, string expected, string actual)
        {
            var expectedLines = expected.Split('\n');
            var actualLines = actual.Split('\n');
            var first = 0;
            while (first < expectedLines.Length && first < actualLines.Length && expectedLines[first] == actualLines[first])
                first++;

            return $"Seed {program.Seed}, command {step} ({program.Commands[step].Kind}), Eggnog {mode} differs from the reference at line {first}:\n"
                + $"  reference: {(first < expectedLines.Length ? expectedLines[first] : "<end>")}\n"
                + $"  eggnog:    {(first < actualLines.Length ? actualLines[first] : "<end>")}\n"
                + $"Reference observation:\n{expected}\nEggnog observation:\n{actual}\nProgram:\n{program}";
        }

        /// <summary>
        /// The number of seeds: <c>EGGNOG_SEEDS</c>, or <see cref="DefaultSeeds"/>.
        /// </summary>
        static int Seeds() =>
            int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_SEEDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeds) && seeds > 0
                ? seeds
                : DefaultSeeds;

        /// <summary>
        /// Every node of a schedule.
        /// </summary>
        static IEnumerable<DiffSchedule> Nodes(DiffSchedule schedule) => schedule.Steps.SelectMany(Nodes).Prepend(schedule);

        /// <summary>
        /// Every term of a fact, nested ones included.
        /// </summary>
        static IEnumerable<DiffTerm> Terms(DiffFact fact)
        {
            var roots = fact.Arguments.Concat(new[] { fact.Left, fact.Right }.Where(t => t is not null).Select(t => t!));
            return roots.SelectMany(Flatten);
        }

        /// <summary>
        /// A term and its subterms.
        /// </summary>
        static IEnumerable<DiffTerm> Flatten(DiffTerm term) => term.Arguments.SelectMany(Flatten).Prepend(term);

    }

}
