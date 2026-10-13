using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Defects found by reviewing the differential suite and the engine fixes it brought. These tests are expected to
    /// fail until the defects are fixed.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class DifferentialReviewTests
    {

        /// <summary>
        /// The rebuild watermark (SortedWritesTable.RebuildWatermark.cs) is kept once per table, while the union-find
        /// positions it is paired with are kept once per rebuilder (<see cref="SubsetTracker"/> is keyed by the
        /// rebuilder's table id). A rebuild against a second union-find moves the watermark past rows that the first
        /// union-find has not canonicalized. The next incremental rebuild against the first union-find never rewrites
        /// them. The full path (tables of at most 10000 rows) does, so the result depends on the table's size again,
        /// which is the very defect the watermark was added to fix.
        /// </summary>
        [Theory]
        [InlineData(10_000)]
        [InlineData(12_000)]
        public void IncrementalRebuildDoesNotDependOnAnotherRebuilder(int rows)
        {
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable());
            var other = db.AddTable(new DisplacedTable());
            var f = db.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(1)], new MinMerge(new ColumnId(1)), ParallelHeuristics.Serial));
            DatabaseTestUtil.Stage(db, uf, [6487, 4553, 0]);
            db.MergeAll();

            // The union is consumed while the table is empty.
            Assert.False(db.ApplyRebuild(uf, [f], 1));

            DatabaseTestUtil.Stage(db, f, Enumerable.Range(0, rows).Select(i => new[] { (uint)i, i % 10 == 0 ? 6487u : (uint)i, 2u }).ToArray());
            db.MergeAll();

            // A rebuild against another union-find, which displaces nothing, and so changes nothing.
            Assert.False(db.ApplyRebuild(other, [f], 3));

            // Every row that mentions 6487 must now be rewritten, whatever the table's size.
            Assert.True(db.ApplyRebuild(uf, [f], 4));
            Assert.DoesNotContain(DatabaseTestUtil.Rows(db, f), r => r[1] == 6487);
        }

        /// <summary>
        /// Every differential rule's first action records every bound variable, so no query has a named variable that
        /// the action does not read, and the planner's handling of such variables is never exercised. A block with no
        /// value variables whose message variables are split across later bags keeps its join constraint only through a
        /// <see cref="MatScanMode.Lookup"/> stage in the result block (the fix of map §8.1 row 5). A bag constrained by
        /// two earlier blocks gets a Lookup epilogue. Neither kind of plan comes up in the generated programs, so the
        /// egglog defect fix of §8.1 row 5 is never compared with the oracle. (Reading a random subset of the variables
        /// alone does not reach it with the generated shapes either. The lost-constraint query of
        /// PlannerDecompositionTests, with five of its nine variables read, gets a Lookup stage on most random data.)
        /// This test inspects the plans of every rule-set query of the default small programs under every
        /// configuration.
        /// </summary>
        [Fact]
        public void DefaultProgramsReachMaterializationLookups()
        {
            var profile = DiffProfile.Small;
            var decomposed = 0;
            var lookups = 0;
            for (var seed = 0; seed < 3000; seed++)
            {
                var program = DiffGenerator.Generate(seed, profile);
                var runner = new DiffRunner(program, profile, new DiffStats());
                foreach (var step in program.Steps)
                {
                    if (step is DiffRunStep run)
                    {
                        var function = runner.Db.AddExternalFunction(new DiffRecorder().Function);
                        var rsb = runner.Db.NewRuleSet();
                        var rules = new List<RuleId>();
                        foreach (var query in run.Queries)
                            foreach (var config in DiffConfig.All)
                                if (runner.AddRule(rsb, query, config, 0, function, Array.Empty<(int, Constraint)>()) is { } id)
                                    rules.Add(id);

                        using (var rs = rsb.Build())
                        {
                            foreach (var id in rules)
                            {
                                if (rs[id].Template is DecomposedPlan plan)
                                {
                                    decomposed++;
                                    var stages = plan.ResultBlock.Concat(plan.Blocks.SelectMany(b => b.Stages));
                                    if (stages.Any(s => s is FusedIntersectMatStage { Mode: MatScanMode.Lookup }))
                                        lookups++;
                                }
                            }
                        }

                        runner.Db.FreeExternalFunction(function);
                    }

                    step.Execute(runner);
                }
            }

            Assert.True(decomposed > 0, "no decomposed plan at all");
            Assert.True(lookups > 0, $"none of {decomposed} decomposed plans has a materialization Lookup stage (map §8.1 row 5)");
        }

        /// <summary>
        /// The eligibility check added to <see cref="StageOrdering"/> (the fix of execute.rs:2399-2470) runs inside the
        /// selection sort's inner loop. Every comparison rescans every unscheduled stage's probes, so the sort costs
        /// O(stages³ × probes) where egglog's costs O(stages²). The executor re-sorts on the hot path, once per frame
        /// at every third position when the stage there is larger than 32 rows. Eligibility depends only on which stages
        /// are still unscheduled, so it could be kept incrementally (a per-atom count of unscheduled probers) at no
        /// asymptotic cost. Quadratic growth makes a sort of four times the stages cost about 16 times as much; this one
        /// costs about 64 times as much.
        /// </summary>
        [Fact]
        public void RunTimeStageSortStaysQuadratic()
        {
            var small = TimeSort(64);
            var large = TimeSort(256);
            Assert.True(large < 32 * small, $"sorting 256 stages took {large / small:0.0} times as long as sorting 64 (quadratic growth gives 16)");
        }

        /// <summary>
        /// A shrunk failing program is printed for reproduction, but <see cref="DiffLoadStep.Format"/> lists at most
        /// <see cref="DiffLoadStep.FormatLimit"/> rows ("the seed reproduces the rest"). The seed regenerates the
        /// original program, not the shrunk one. A shrunk bulk or huge program that still loads more than 200 rows into
        /// one table is therefore printed incompletely, and cannot be rebuilt from the listing.
        /// </summary>
        [Fact]
        public void LoadListingsAreComplete()
        {
            var rows = Enumerable.Range(0, 300).Select(i => new[] { (uint)i, (uint)(i + 1) }).ToArray();
            var program = new DiffProgram(1, "bulk", "Serial", 1000, [new DiffTable(0, 1, 2, false, DiffMergeKind.TakeNew, 1, false, [], false, [], [])], []);
            var listing = new DiffLoadStep(0, rows).Format(program);
            Assert.Contains(TableTestUtil.Show(rows[^1]), listing);
        }

        /// <summary>
        /// Times one run-time stage sort of a Free Join path of <paramref name="n"/> stages, each probing the next one's
        /// cover: the best of several rounds.
        /// </summary>
        static double TimeSort(int n)
        {
            var stages = Enumerable.Range(0, n).Select(i => (JoinStage)new FusedIntersectStage(
                new ScanSpec(new SubAtom(new AtomId(i), [new ColumnId(0), new ColumnId(1)]), []),
                [new StageBinding(new ColumnId(0), new Variable(i)), new StageBinding(new ColumnId(1), new Variable(i + 1))],
                i < n - 1 ? [new Probe(new ScanSpec(new SubAtom(new AtomId(i + 1), [new ColumnId(0)]), []), [new ColumnId(1)])] : [])).ToArray();
            var rng = new Random(1);
            var sizes = new FixedSizes(Enumerable.Range(0, n).Select(_ => rng.Next(100, 1000)).ToArray());
            var order = new InstrOrder(n);
            var leaf = new bool[n];
            var refined = new long[n];
            var reps = Math.Max(4, 400_000 / (n * n));
            var best = double.MaxValue;
            for (var round = 0; round < 5; round++)
            {
                var clock = Stopwatch.StartNew();
                for (var r = 0; r < reps; r++)
                {
                    order.Reset(n);
                    StageOrdering.SortPlanBySize(order, leaf, 0, stages, ref sizes, refined);
                }

                best = Math.Min(best, clock.Elapsed.TotalMilliseconds / reps);
            }

            return best;
        }

    }

}
