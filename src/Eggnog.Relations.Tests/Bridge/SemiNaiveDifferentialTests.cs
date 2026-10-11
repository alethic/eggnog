using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Differential tests of the rule layer (map §6, M8): random programs of rules and facts run semi-naively, naively,
    /// and through a naive evaluator over <see cref="ReferenceView"/> (match the closed snapshot, apply every write,
    /// close again), which must agree after every iteration. Programs that mint ids, which the reference cannot follow,
    /// compare semi-naive and naive runs by their counts.
    /// </summary>
    /// <remarks>
    /// Both engines and the reference keep the smallest id of a class canonical and no rule mints ids, so the views
    /// must agree exactly: the union-find on every id, and every table row for row. Semi-naive evaluation must find
    /// exactly the facts naive evaluation finds, iteration by iteration (an iteration is Jacobi: every rule matches the
    /// same snapshot), and report the same changes. The rules join a constructor, a relation and a lattice function,
    /// with constants, repeated variables, a left-hand-side primitive filter, unions and writes.
    /// </remarks>
    public class SemiNaiveDifferentialTests
    {

        const int Iterations = 6;

        public static TheoryData<int> Seeds()
        {
            var data = new TheoryData<int>();
            for (var seed = 1; seed <= 40; seed++)
                data.Add(seed);

            return data;
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void SemiNaiveNaiveAndTheReferenceAgree(int seed)
        {
            Run(seed, ParallelHeuristics.Serial);
        }

        /// <summary>
        /// Guards against a vacuous generator: across the seeds, the rules match, change the database over several
        /// iterations, and semi-naive runs match less than naive ones.
        /// </summary>
        [Fact]
        public void TheRandomProgramsExerciseTheRules()
        {
            var (changed, semiMatches, naiveMatches) = (0, 0L, 0L);
            for (var seed = 1; seed <= 40; seed++)
            {
                var stats = Run(seed, ParallelHeuristics.Serial);
                changed += stats.ChangedIterations;
                semiMatches += stats.SemiMatches;
                naiveMatches += stats.NaiveMatches;
            }

            Assert.True(changed >= 60, $"only {changed} iterations changed anything");
            Assert.True(semiMatches >= 200, $"only {semiMatches} semi-naive matches");
            Assert.True(semiMatches < naiveMatches, $"{semiMatches} semi-naive matches, {naiveMatches} naive");
        }

        [Fact]
        public void SemiNaiveNaiveAndTheReferenceAgreeWithParallelSettings()
        {
            Run(41, new ParallelHeuristics(4));
            Run(42, new ParallelHeuristics(4));
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void MintingProgramsAgreeSemiNaivelyAndNaively(int seed)
        {
            var random = new Random(seed);
            var semi = new RuleProgram(ParallelHeuristics.Serial);
            var naive = new RuleProgram(ParallelHeuristics.Serial);
            var trace = new StringBuilder();
            LoadFacts(random, semi, naive, null, trace);

            var rules = Enumerable.Range(0, 4).Select(_ => RandomRule.Generate(random, semi.Ids, semi.Values, semi.Unit, mint: true)).ToArray();
            var semiRules = BuildRules(semi, rules, true, trace);
            var naiveRules = BuildRules(naive, rules, false, null);

            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var semiReport = semi.EGraph.RunRules(semiRules);
                var naiveReport = naive.EGraph.RunRules(naiveRules);
                var where = $"iteration {iteration} of\n{trace}";
                Assert.True(semiReport.Changed == naiveReport.Changed, $"changed {semiReport.Changed} semi-naively, {naiveReport.Changed} naively, {where}");

                // The runs may mint different numbers of ids: each batch of matches has its own cache of minted keys
                // (decision 5), and the batches differ; duplicates are unioned, so the number of classes agrees.
                var semiClasses = semi.EGraph.Database.ReadCounter(semi.EGraph.IdCounter) - semi.EGraph.UnionCount;
                var naiveClasses = naive.EGraph.Database.ReadCounter(naive.EGraph.IdCounter) - naive.EGraph.UnionCount;
                Assert.True(semiClasses == naiveClasses, $"{semiClasses} classes semi-naively, {naiveClasses} naively, {where}");
                for (var f = 0; f < semi.Functions.Length; f++)
                    Assert.True(semi.EGraph.TableSize(semi.Functions[f]) == naive.EGraph.TableSize(naive.Functions[f]), $"{RandomRule.Names[f]} holds {semi.EGraph.TableSize(semi.Functions[f])} rows semi-naively, {naive.EGraph.TableSize(naive.Functions[f])} naively, {where}");
            }
        }

        static (int ChangedIterations, long SemiMatches, long NaiveMatches) Run(int seed, ParallelHeuristics parallelism)
        {
            var random = new Random(seed);
            var semi = new RuleProgram(parallelism);
            var naive = new RuleProgram(parallelism);
            var reference = new ReferenceView(semi.Tables);
            var trace = new StringBuilder();
            LoadFacts(random, semi, naive, reference, trace);
            reference.Close();
            Check(semi, reference, semi.Tables, "semi-naive", "after loading", trace);
            Check(naive, reference, semi.Tables, "naive", "after loading", trace);

            var rules = Enumerable.Range(0, 4).Select(_ => RandomRule.Generate(random, semi.Ids, semi.Values, semi.Unit)).ToArray();
            var semiRules = BuildRules(semi, rules, true, trace);
            var naiveRules = BuildRules(naive, rules, false, null);

            var (changed, semiMatches, naiveMatches) = (0, 0L, 0L);
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var semiReport = semi.EGraph.RunRules(semiRules);
                var naiveReport = naive.EGraph.RunRules(naiveRules);
                Step(reference, semi.Tables, rules);
                changed += semiReport.Changed ? 1 : 0;
                for (var i = 0; i < rules.Length; i++)
                {
                    semiMatches += semiReport.RuleSetReport.NumMatches("r" + i);
                    naiveMatches += naiveReport.RuleSetReport.NumMatches("r" + i);
                }

                var where = $"iteration {iteration}";
                Check(semi, reference, semi.Tables, "semi-naive", where, trace);
                Check(naive, reference, semi.Tables, "naive", where, trace);
                Assert.True(semiReport.Changed == naiveReport.Changed, $"changed {semiReport.Changed} semi-naively, {naiveReport.Changed} naively, at {where} of\n{trace}");
            }

            Assert.False(semi.EGraph.TryTakePanic(out var message), message);
            return (changed, semiMatches, naiveMatches);
        }

        /// <summary>
        /// Loads the same random facts into the engines (and the reference): rows over canonical ids, and unions.
        /// </summary>
        static void LoadFacts(Random random, RuleProgram semi, RuleProgram naive, ReferenceView? reference, StringBuilder trace)
        {
            uint Pick() => semi.EGraph.GetCanonRepr(new Value(semi.Ids[random.Next(semi.Ids.Length)]), ColumnTy.Id).Rep;

            for (var i = 0; i < 30; i++)
            {
                var roll = random.Next(10);
                if (roll < 1)
                {
                    var (a, b) = (Pick(), Pick());
                    Union(semi.EGraph, new Value(a), new Value(b));
                    Union(naive.EGraph, new Value(a), new Value(b));
                    reference?.Union(a, b);
                    trace.AppendLine($"union({a}, {b})");
                    continue;
                }

                var function = roll < 3 ? 0 : roll < 8 ? 1 : 2;
                var row = new uint[RandomRule.KeyCounts[function] + 1];
                for (var c = 0; c < row.Length; c++)
                {
                    row[c] = (c < RandomRule.KeyCounts[function] ? 'I' : RandomRule.OutputKinds[function]) switch
                    {
                        'I' => Pick(),
                        'U' => semi.Unit,
                        _ => semi.Values[random.Next(semi.Values.Length)],
                    };
                }

                semi.EGraph.AddValues([(semi.Functions[function], row.Select(v => new Value(v)).ToArray())]);
                naive.EGraph.AddValues([(naive.Functions[function], row.Select(v => new Value(v)).ToArray())]);
                if (reference is not null)
                {
                    reference.Add(semi.Tables[function], row);
                    reference.Close();
                }

                trace.AppendLine($"{RandomRule.Names[function]}({string.Join(", ", row)})");
            }
        }

        /// <summary>
        /// Builds the rules into a program, named <c>r0</c>, <c>r1</c>, ...
        /// </summary>
        static EGraphRuleId[] BuildRules(RuleProgram program, RandomRule[] rules, bool semiNaive, StringBuilder? trace)
        {
            var ids = new EGraphRuleId[rules.Length];
            for (var i = 0; i < rules.Length; i++)
            {
                ids[i] = rules[i].Build(program, semiNaive, "r" + i);
                trace?.AppendLine($"r{i}: {rules[i]}");
            }

            return ids;
        }

        /// <summary>
        /// One Jacobi iteration of the reference: every rule matches the closed snapshot, then every write and union is
        /// applied and the view closed.
        /// </summary>
        static void Step(ReferenceView reference, ReferenceTable[] tables, RandomRule[] rules)
        {
            var snapshot = tables.Select(t => (IReadOnlyList<uint[]>)reference.Rows(t).Select(r => (uint[])r.Clone()).ToList()).ToArray();
            var writes = new List<(int Function, uint[] Row)>();
            var unions = new List<(uint, uint)>();
            foreach (var rule in rules)
                rule.Evaluate(snapshot, writes, unions);

            foreach (var (function, row) in writes)
                reference.Add(tables[function], row);

            foreach (var (a, b) in unions)
                reference.Union(a, b);

            reference.Close();
        }

        static void Check(RuleProgram program, ReferenceView reference, ReferenceTable[] tables, string mode, string where, StringBuilder trace)
        {
            foreach (var id in program.Ids)
            {
                var expected = reference.Find(id);
                var actual = program.EGraph.GetCanonRepr(new Value(id), ColumnTy.Id).Rep;
                if (expected != actual)
                    Assert.Fail($"{mode}: find({id}) is {actual}, expected {expected}, {where} of\n{trace}");
            }

            for (var f = 0; f < program.Functions.Length; f++)
            {
                var expected = reference.Rows(tables[f]).Select(r => string.Join(" ", r)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                var actual = Rows(program.EGraph, program.Functions[f]).Select(r => string.Join(" ", r.Values.Select(v => v.Rep))).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                if (expected.SequenceEqual(actual) == false)
                    Assert.Fail($"{mode}: {RandomRule.Names[f]} holds [{string.Join("; ", actual)}], expected [{string.Join("; ", expected)}], {where} of\n{trace}");
            }
        }

    }

}
