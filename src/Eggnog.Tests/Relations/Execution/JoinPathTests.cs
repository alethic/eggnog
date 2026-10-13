using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Targeted runs of join paths the random tests reach only by chance: run-time reordering deep in a plan, and
    /// multi-column probes through dynamic and cached tuple indexes, clamped and not.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class JoinPathTests
    {

        public static IEnumerable<object[]> ModesData => ExecutionTestUtil.Modes.Select(m => new object[] { m });

        /// <summary>
        /// The star <c>T(x, y), S(x, z), R(x, w)</c> with two values of <c>x</c>, each in 50 rows of every table: after
        /// position 0 binds <c>x</c>, the stage at position 1 still has 50 candidates, more than 32, so the executor
        /// re-sorts the remaining stages (core-relations/src/free_join/execute.rs:972-979).
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void RuntimeReorderingKeepsTheResults(string mode)
        {
            foreach (var strategy in new[] { PlanStrategy.Gj, PlanStrategy.PureSize, PlanStrategy.MinCover })
            {
                var db = ExecutionTestUtil.NewDatabase(mode);
                var tables = Enumerable.Range(0, 3).Select(_ => db.AddTable(Strict(2, 2))).ToArray();
                for (var k = 0; k < 3; k++)
                    DatabaseTestUtil.Stage(db, tables[k], Enumerable.Range(0, 100).Select(i => new[] { (uint)(i % 2), (uint)(100 * k + i) }).ToArray());

                db.MergeAll();
                var log = new List<uint[]>();
                var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                q.Strategy = strategy;
                q.NoDecomposition = true;
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                var z = q.NewVariable("z");
                var w = q.NewVariable("w");
                q.AddAtom(tables[0], [x, y]);
                q.AddAtom(tables[1], [x, z]);
                q.AddAtom(tables[2], [x, w]);
                var rb = q.Build();
                rb.CallExternal(recorder, [x, y, z, w]);
                rb.Build("star");
                using var rs = rsb.Build();
                var report = db.RunRuleSet(rs);

                // 2 values of x times 50 * 50 * 50 combinations, each exactly once.
                Assert.Equal(2 * 50 * 50 * 50, log.Count);
                Assert.Equal(log.Count, ExecutionTestUtil.Distinct(log).Count);
                Assert.All(log, t => Assert.True(t[1] % 2 == t[0] && t[2] % 2 == t[0] && t[3] % 2 == t[0]));
                Assert.Equal(log.Count, report.NumMatches("star"));
            }
        }

        /// <summary>
        /// A Free Join step probing a two-column index: through the database's cached tuple index when the probed atom
        /// is the whole table, clamped to the range when it is a recent timestamp range of more than half the table, and
        /// through a dynamic tuple index when the range is smaller; with tombstones in each.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void MultiColumnProbesUseTupleIndexes(string mode)
        {
            foreach (var since in new uint[] { 0, 1, 3 })
            {
                foreach (var strategy in new[] { PlanStrategy.PureSize, PlanStrategy.MinCover })
                {
                    var db = ExecutionTestUtil.NewDatabase(mode);
                    var cover = db.AddTable(Strict(3, 3));
                    var probed = db.AddTable(new SortedWritesTable(2, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));

                    // A small cover and a large probed table, each with a variable of its own, so the planner covers with
                    // the former and probes the latter on (a, b). Each timestamp adds 60 new keys to the probed table.
                    var coverRows = from x in new uint[] { 1, 7 } from y in Enumerable.Range(0, 40) where y % 10 < 5 select new[] { x, (uint)y, 50u + (uint)y };
                    DatabaseTestUtil.Stage(db, cover, coverRows.ToArray());
                    for (uint ts = 0; ts < 4; ts++)
                    {
                        DatabaseTestUtil.Stage(db, probed, Enumerable.Range(0, 60).Select(i => new[] { (uint)(i % 12), 10 * ts + (uint)(i / 12), ts }).ToArray());
                        db.MergeAll();
                    }

                    db.GetTableInfo(probed).GetIndex([C(0), C(1)]);
                    DatabaseTestUtil.StageRemove(db, probed, [1, 0], [7, 11], [1, 22], [7, 33], [7, 34]);
                    db.MergeAll();

                    var log = new List<uint[]>();
                    var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
                    var rsb = db.NewRuleSet();
                    var q = rsb.NewRule();
                    q.Strategy = strategy;
                    var a = q.NewVariable("a");
                    var b = q.NewVariable("b");
                    var c = q.NewVariable("c");
                    var t = q.NewVariable("t");
                    q.AddAtom(cover, [a, b, c]);
                    q.AddAtom(probed, [a, b, t], [Constraint.GeConst(C(2), V(since))]);
                    var rb = q.Build();
                    rb.CallExternal(recorder, [a, b, c, t]);
                    rb.Build();
                    using var rs = rsb.Build();
                    db.RunRuleSet(rs);

                    var covered = DatabaseTestUtil.Rows(db, cover);
                    var probedRows = DatabaseTestUtil.Rows(db, probed).Where(p => p[2] >= since);
                    var expected = from r in covered from p in probedRows where r[0] == p[0] && r[1] == p[1] select new[] { r[0], r[1], r[2], p[2] };
                    Assert.Equal(ExecutionTestUtil.Distinct(expected), ExecutionTestUtil.Distinct(log));
                }
            }
        }

        /// <summary>
        /// Probes with five-column keys, wider than the join's initial key scratch, behind a first stage, so that the
        /// probing stage runs in morsels handed to other tasks in the parallel modes.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void WideKeysProbeCorrectly(string mode)
        {
            foreach (var strategy in new[] { PlanStrategy.PureSize, PlanStrategy.MinCover, PlanStrategy.Gj })
            {
                var db = ExecutionTestUtil.NewDatabase(mode);
                var gate = db.AddTable(Strict(1, 1));
                var left = db.AddTable(Strict(6, 6));
                var right = db.AddTable(Strict(6, 6));
                DatabaseTestUtil.Stage(db, gate, [0], [1], [2]);
                DatabaseTestUtil.Stage(db, left, Enumerable.Range(0, 300).Select(i => new[] { (uint)(i % 2), (uint)(i % 3), (uint)(i % 5), (uint)(i % 7), (uint)(i % 11), (uint)(i % 4) }).ToArray());
                DatabaseTestUtil.Stage(db, right, Enumerable.Range(0, 900).Select(i => new[] { (uint)(i % 2), (uint)(i % 3), (uint)(i % 5), (uint)(i % 7), (uint)(i % 13), 5000u + (uint)i }).ToArray());
                db.MergeAll();
                var log = new List<uint[]>();
                var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                q.Strategy = strategy;
                var v = Enumerable.Range(0, 7).Select(i => q.NewVariable("v" + i)).ToArray();
                q.AddAtom(gate, [v[5]]);
                q.AddAtom(left, [v[0], v[1], v[2], v[3], v[4], v[5]]);
                q.AddAtom(right, [v[0], v[1], v[2], v[3], v[4], v[6]]);
                var rb = q.Build();
                rb.CallExternal(recorder, [v[0], v[1], v[2], v[3], v[4], v[5], v[6]]);
                rb.Build();
                using var rs = rsb.Build();
                db.RunRuleSet(rs);

                var l = DatabaseTestUtil.Rows(db, left).Where(row => row[5] < 3);
                var r = DatabaseTestUtil.Rows(db, right);
                var expected = from x in l from y in r where x.Take(5).SequenceEqual(y.Take(5)) select x.Concat([y[5]]).ToArray();
                Assert.NotEmpty(expected);
                Assert.Equal(ExecutionTestUtil.Distinct(expected), ExecutionTestUtil.Distinct(log));
            }
        }

    }

}
