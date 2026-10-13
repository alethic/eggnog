using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The contract of <see cref="Database.RunRuleSet"/> beyond the join's denotation: reports, batching, factorized
    /// leaf scans, staged writes and the merge, early stop, failures, determinism and the parallel path on a database
    /// large enough to choose it by itself.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class ExecutorBehaviorTests
    {

        public static IEnumerable<object[]> ModesData => ExecutionTestUtil.Modes.Select(m => new object[] { m });

        [Fact]
        public void AnEmptyRuleSetDoesNotMerge()
        {
            var db = ExecutionTestUtil.NewDatabase("Serial");
            var t = db.AddTable(Strict(1, 1));
            DatabaseTestUtil.Stage(db, t, [1]);
            using var rs = db.NewRuleSet().Build();
            var report = db.RunRuleSet(rs);
            Assert.Same(RuleSetReport.Empty, report);

            // egglog returns before merge_all, so the staged row is still pending.
            Assert.Empty(Rows(db, t));
            db.MergeAll();
            Assert.Equal(["[1]"], Rows(db, t));
        }

        [Theory]
        [MemberData(nameof(ModesData))]
        public void ARuleWithoutAtomsFiresOnce(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var u = db.AddTable(Strict(1, 1));
            var rsb = db.NewRuleSet();
            var rb = rsb.NewRule().Build();
            rb.Insert(u, [V(3)]);
            rb.Build("fact");
            using var rs = rsb.Build();
            var report = db.RunRuleSet(rs);
            Assert.True(report.Changed);
            Assert.Equal(1, report.NumMatches("fact"));
            Assert.Equal(["[3]"], Rows(db, u));
        }

        [Theory]
        [MemberData(nameof(ModesData))]
        public void MatchesSpanSeveralBatches(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var t = db.AddTable(Strict(1, 2));
            var u = db.AddTable(Strict(1, 2));
            DatabaseTestUtil.Stage(db, t, Enumerable.Range(0, 1000).Select(i => new[] { (uint)i, (uint)(i * 3) }).ToArray());
            db.MergeAll();
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            q.AddAtom(t, [x, y]);
            var rb = q.Build();
            rb.Insert(u, [y, x]);
            rb.Build("copy");
            using var rs = rsb.Build();
            var report = db.RunRuleSet(rs);
            Assert.True(report.Changed);
            Assert.Equal(1000, report.NumMatches("copy"));
            Assert.Equal(1000, db.GetTable(u).Count);
            Assert.All(DatabaseTestUtil.Rows(db, u), r => Assert.Equal(r[0], r[1] * 3));

            // Nothing new the second time.
            using var again = Copy(db, t, u);
            Assert.False(db.RunRuleSet(again).Changed);
        }

        [Theory]
        [MemberData(nameof(ModesData))]
        public void IndependentScansAreCrossedAtTheAction(string mode)
        {
            foreach (var strategy in new[] { PlanStrategy.Gj, PlanStrategy.PureSize, PlanStrategy.MinCover })
            {
                var db = ExecutionTestUtil.NewDatabase(mode);
                var t = db.AddTable(Strict(1, 1));
                var s = db.AddTable(Strict(1, 1));
                var r = db.AddTable(Strict(1, 1));
                DatabaseTestUtil.Stage(db, t, Enumerable.Range(0, 30).Select(i => new[] { (uint)i }).ToArray());
                DatabaseTestUtil.Stage(db, s, Enumerable.Range(100, 20).Select(i => new[] { (uint)i }).ToArray());
                DatabaseTestUtil.Stage(db, r, [7], [8]);
                db.MergeAll();
                var log = new List<uint[]>();
                var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                q.Strategy = strategy;
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                var z = q.NewVariable("z");
                q.AddAtom(t, [x]);
                q.AddAtom(s, [y]);
                q.AddAtom(r, [z]);
                var rb = q.Build();
                rb.CallExternal(recorder, [x, y, z]);
                rb.Build("cross");
                using var rs = rsb.Build();
                var report = db.RunRuleSet(rs);

                // Every combination exactly once: leaf scans multiply out without duplicates.
                Assert.Equal(30 * 20 * 2, log.Count);
                Assert.Equal(30 * 20 * 2, ExecutionTestUtil.Distinct(log).Count);
                Assert.Equal(log.Count, report.NumMatches("cross"));
            }
        }

        [Theory]
        [MemberData(nameof(ModesData))]
        public void ReportsCarryPlansWhenAsked(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var t = db.AddTable(Strict(1, 1));
            DatabaseTestUtil.Stage(db, t, [1]);
            db.MergeAll();
            foreach (var level in new[] { ReportLevel.TimeOnly, ReportLevel.WithPlan, ReportLevel.StageInfo })
            {
                var rsb = db.NewRuleSet();
                for (var i = 0; i < 2; i++)
                {
                    var q = rsb.NewRule();
                    var x = q.NewVariable("x");
                    q.AddAtom(t, [x]);
                    var rb = q.Build();
                    rb.Insert(t, [x]);
                    rb.Build("same");
                }

                using var rs = rsb.Build();
                var report = db.RunRuleSet(rs, level);
                var reports = report.RuleReports["same"];
                Assert.Equal(2, reports.Count);
                Assert.Equal(2, report.NumMatches("same"));
                Assert.All(reports, r => Assert.Equal(level == ReportLevel.TimeOnly, r.Plan is null));
                if (level != ReportLevel.TimeOnly)
                    Assert.StartsWith("SinglePlan", reports[0].Plan);
            }
        }

        [Theory]
        [MemberData(nameof(ModesData))]
        public void ActionsRunOnTheRunsTimestamp(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var t = db.AddTable(Strict(1, 1));
            DatabaseTestUtil.Stage(db, t, Enumerable.Range(0, 300).Select(i => new[] { (uint)i }).ToArray());
            db.MergeAll();
            var stamps = new HashSet<uint>();
            var stamp = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                lock (stamps)
                    stamps.Add(state.Timestamp);

                result = 0;
                return true;
            }));

            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            q.AddAtom(t, [x]);
            var rb = q.Build();
            rb.CallExternal(stamp, [x]);
            rb.Build();
            using var rs = rsb.Build();
            db.RunRuleSet(rs, timestamp: 42);
            Assert.Equal([42u], stamps);
        }

        [Theory]
        [MemberData(nameof(ModesData))]
        public void FailuresAreRecordedAndStopTheRun(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var t = db.AddTable(Strict(1, 1));
            DatabaseTestUtil.Stage(db, t, Enumerable.Range(0, 5000).Select(i => new[] { (uint)i }).ToArray());
            db.MergeAll();
            var calls = 0;
            var fail = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                System.Threading.Interlocked.Increment(ref calls);
                state.ReportFailure("boom");
                result = 0;
                return false;
            }));

            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            q.AddAtom(t, [x]);
            var rb = q.Build();
            rb.CallExternal(fail, [x]);
            rb.Build("failing");
            using var rs = rsb.Build();
            var report = db.RunRuleSet(rs);
            Assert.Equal("boom", db.TakeFailure());
            Assert.Equal(0, report.NumMatches("failing"));

            // Serially, the first full batch fails every lane and stops the join before the next match. In parallel,
            // batches already handed to the scope still run, as in egglog (on one thread, every batch is formed before
            // the first one runs).
            if (mode == "Serial")
                Assert.Equal(Bindings.DefaultBatchSize, calls);
            else
                Assert.InRange(calls, Bindings.DefaultBatchSize, 5000);
        }

        [Fact]
        public void SerialRunsAreDeterministic()
        {
            // Fresh ids are minted per batch; a serial run mints them in the same order every time, so serial execution
            // is deterministic for a given program (parallel runs may number ids differently).
            string Run()
            {
                var m = new ExecutionMathEgraph("Serial");
                AcSaturation.Run(m.Db, m.Num, m.Add, m.Uf, m.IdCounter, PlanStrategy.PureSize, 4);
                return DatabaseTestUtil.Dump(m.Db, m.Add) + " | " + DatabaseTestUtil.Dump(m.Db, m.Uf);
            }

            Assert.Equal(Run(), Run());
        }

        [Theory]
        [MemberData(nameof(ModesData))]
        public void SaturationIsTheSameInEveryMode(string mode)
        {
            // Fresh ids differ between the modes, but with commutativity matching the whole recent range the saturated
            // e-graph is the same up to renaming: one class per sum of two or more of the n leaves, and a congruent,
            // canonical add table.
            foreach (var n in new[] { 4, 5, 6 })
            {
                var g = new ExecutionMathEgraph(mode);
                var (left, right) = AcSaturation.Run(g.Db, g.Num, g.Add, g.Uf, g.IdCounter, PlanStrategy.MinCover, n, wholeRecentRange: true);
                Assert.Equal(g.Find(left), g.Find(right));
                var adds = DatabaseTestUtil.Rows(g.Db, g.Add);
                Assert.All(adds, r => Assert.True(g.Find(r[0]) == r[0] && g.Find(r[1]) == r[1] && g.Find(r[2]) == r[2]));
                Assert.Equal((1 << n) - n - 1, adds.Select(r => r[2]).Distinct().Count());
            }
        }

        [Fact]
        public void LargeDatabasesChooseTheParallelPathAndAgreeWithTheSerialOne()
        {
            // Over ParallelHeuristics.DbLevelThreshold rows, a database with several threads runs rule sets in parallel
            // without the test hook; the derived facts must be the serial run's.
            string Run(ParallelHeuristics parallelism)
            {
                var db = new Database(parallelism);
                var edges = db.AddTable(new SortedWritesTable(2, 3, new ColumnId(2), ReadOnlySpan<ColumnId>.Empty, new ClosureMerge((current, _) => null), ParallelHeuristics.Serial));
                var rng = new Random(7);
                var initial = new HashSet<(uint, uint)>();
                while (initial.Count < 12_000)
                    initial.Add(((uint)rng.Next(8000), (uint)rng.Next(8000)));

                DatabaseTestUtil.Stage(db, edges, initial.Select(e => new[] { e.Item1, e.Item2, 0u }).ToArray());
                db.MergeAll();
                Assert.True(db.TotalSizeEstimate > ParallelHeuristics.DbLevelThreshold);

                // Two semi-naive rounds of edge(x, y), edge(y, z), new on either side => edge(x, z).
                for (uint round = 0; round < 2; round++)
                {
                    var rsb = db.NewRuleSet();
                    foreach (var newFirst in new[] { true, false })
                    {
                        var q = rsb.NewRule();
                        var x = q.NewVariable("x");
                        var y = q.NewVariable("y");
                        var z = q.NewVariable("z");
                        var t1 = q.NewVariable("t1");
                        var t2 = q.NewVariable("t2");
                        q.AddAtom(edges, [x, y, t1], [newFirst ? Constraint.GeConst(new ColumnId(2), V(round)) : Constraint.LtConst(new ColumnId(2), V(round))]);
                        q.AddAtom(edges, [y, z, t2], newFirst ? [] : [Constraint.GeConst(new ColumnId(2), V(round))]);
                        var rb = q.Build();
                        rb.Insert(edges, [x, z, V(round + 1)]);
                        rb.Build("path");
                    }

                    using var rs = rsb.Build();
                    db.RunRuleSet(rs);
                }

                return string.Join(";", DatabaseTestUtil.Rows(db, edges).Select(r => r[0] + "," + r[1]).OrderBy(s => s, StringComparer.Ordinal));
            }

            Assert.Equal(Run(ParallelHeuristics.Serial), Run(new ParallelHeuristics(4)));
        }

        static RuleSet Copy(Database db, TableId t, TableId u)
        {
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            q.AddAtom(t, [x, y]);
            var rb = q.Build();
            rb.Insert(u, [y, x]);
            rb.Build("copy");
            return rsb.Build();
        }

    }

}
