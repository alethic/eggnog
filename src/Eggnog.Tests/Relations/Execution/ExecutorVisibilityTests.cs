using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The executor never lets a row the table does not show reach an action, on any path: egglog's two stale-row holes
    /// (an atom without variables checks only that its header is non-empty, and a scan with constraints does not skip
    /// tombstones) and the cached-index paths that must refine for visibility.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class ExecutorVisibilityTests
    {

        public static IEnumerable<object[]> ModesData => ExecutionTestUtil.Modes.Select(m => new object[] { m });

        /// <summary>
        /// An atom without variables checks that a visible row exists: <c>T(1, 2) =&gt; U(7)</c> must not fire after
        /// <c>T(1, 2)</c> is deleted. The atom has no variables, so no stage scans it, and its header subset (the
        /// cached column index's rows for the constant, refreshed incrementally) still lists the tombstone; egglog,
        /// which checks only that the header is non-empty, fires the rule.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void AtomWithoutVariablesIgnoresDeletedRows(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var t = db.AddTable(Strict(1, 2));
            var u = db.AddTable(Strict(1, 1));
            DatabaseTestUtil.Stage(db, t, [1, 2], [3, 4]);
            db.MergeAll();

            // Build the column indexes while T(1, 2) is live, then delete it: they keep listing its row.
            db.GetTableInfo(t).GetColumnIndex(C(0));
            db.GetTableInfo(t).GetColumnIndex(C(1));
            DatabaseTestUtil.StageRemove(db, t, [1]);
            db.MergeAll();
            Assert.Equal(["[3, 4]"], Rows(db, t));

            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            q.AddAtom(t, [V(1), V(2)]);
            var rb = q.Build();
            rb.Insert(u, [V(7)]);
            rb.Build("deleted");

            q = rsb.NewRule();
            q.AddAtom(t, [V(3), V(4)]);
            rb = q.Build();
            rb.Insert(u, [V(8)]);
            rb.Build("live");
            using var rs = rsb.Build();

            // The deleted fact's header is not empty: egglog would fire on it.
            var deleted = rs[new RuleId(0)];
            Assert.NotNull(deleted.Instance);
            Assert.Single(deleted.Template.ExistenceChecks);
            Assert.All(deleted.Instance.HeaderSubsets, s => Assert.True(s.Size > 0));

            var report = db.RunRuleSet(rs);
            Assert.Equal(0, report.NumMatches("deleted"));
            Assert.Equal(1, report.NumMatches("live"));
            Assert.Equal(["[8]"], Rows(db, u));
        }

        /// <summary>
        /// The same existence check with slow constraints: an atom of wildcards with a constraint no live row satisfies
        /// must not let the rule fire, though its header is the whole non-empty table.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void AtomWithoutVariablesAppliesItsSlowConstraints(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var t = db.AddTable(Strict(1, 2));
            var u = db.AddTable(Strict(1, 1));
            DatabaseTestUtil.Stage(db, t, [1, 2], [3, 4]);
            db.MergeAll();

            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            q.AddAtom(t, [QueryEntry.Wildcard(), QueryEntry.Wildcard()], [Constraint.GtConst(C(1), V(5))]);
            var rb = q.Build();
            rb.Insert(u, [V(1)]);
            rb.Build("none");

            q = rsb.NewRule();
            q.AddAtom(t, [QueryEntry.Wildcard(), QueryEntry.Wildcard()], [Constraint.GtConst(C(1), V(3))]);
            rb = q.Build();
            rb.Insert(u, [V(2)]);
            rb.Build("some");
            using var rs = rsb.Build();

            db.RunRuleSet(rs);
            Assert.Equal(["[2]"], Rows(db, u));
        }

        /// <summary>
        /// An atom without variables next to atoms with variables: the existence check gates the whole rule.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void ExistenceChecksGateTheRule(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var flag = db.AddTable(Strict(1, 1));
            var t = db.AddTable(Strict(1, 2));
            var u = db.AddTable(Strict(1, 1));
            DatabaseTestUtil.Stage(db, flag, [1]);
            DatabaseTestUtil.Stage(db, t, [1, 2], [3, 4]);
            db.MergeAll();
            DatabaseTestUtil.StageRemove(db, flag, [1]);
            db.MergeAll();

            foreach (var (present, expected) in new[] { (false, Array.Empty<string>()), (true, new[] { "[1]", "[3]" }) })
            {
                if (present)
                {
                    DatabaseTestUtil.Stage(db, flag, [1]);
                    db.MergeAll();
                }

                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                var x = q.NewVariable("x");
                q.AddAtom(flag, [QueryEntry.Wildcard()]);
                q.AddAtom(t, [x, QueryEntry.Wildcard()]);
                var rb = q.Build();
                rb.Insert(u, [x]);
                rb.Build();
                using var rs = rsb.Build();
                db.RunRuleSet(rs);
                Assert.Equal(expected, Rows(db, u));
            }
        }

        /// <summary>
        /// A scan with a slow constraint on a column other than 0 must skip tombstones (whose other cells still hold
        /// their values), in a plain scan and as the cover of a Free Join step; egglog's constrained scans skip the
        /// tombstone check.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void ConstrainedScansSkipTombstones(string mode)
        {
            foreach (var strategy in new[] { PlanStrategy.Gj, PlanStrategy.PureSize, PlanStrategy.MinCover })
            {
                var db = ExecutionTestUtil.NewDatabase(mode);
                var t = db.AddTable(Strict(1, 2));
                var s = db.AddTable(Strict(1, 1));
                var u = db.AddTable(Strict(1, 1));
                DatabaseTestUtil.Stage(db, t, Enumerable.Range(0, 40).Select(i => new[] { (uint)i, (uint)(i % 5) }).ToArray());
                DatabaseTestUtil.Stage(db, s, [1], [2], [3], [4]);
                db.MergeAll();
                DatabaseTestUtil.StageRemove(db, t, Enumerable.Range(0, 40).Where(i => i % 3 == 0).Select(i => new[] { (uint)i }).ToArray());
                db.MergeAll();
                Assert.True(db.GetTable(t).HasStaleRows);

                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                q.Strategy = strategy;
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                q.AddAtom(t, [x, y], [Constraint.GtConst(C(1), V(0))]);
                q.AddAtom(s, [y]);
                var rb = q.Build();
                rb.Insert(u, [x]);
                rb.Build();
                using var rs = rsb.Build();
                db.RunRuleSet(rs);

                var expected = Enumerable.Range(0, 40).Where(i => i % 3 != 0 && i % 5 != 0).Select(i => $"[{i}]").OrderBy(r => r, StringComparer.Ordinal);
                Assert.Equal(expected, Rows(db, u).OrderBy(r => r, StringComparer.Ordinal));
            }
        }

        /// <summary>
        /// The database's cached column index is refreshed incrementally, so after deletions and updates it lists rows
        /// the table no longer shows. Probing it (an atom covering most of the table) must refine every result for
        /// visibility, on the eager path (at most 16 rows per value) and through the child cache (more).
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void CachedIndexResultsAreRefinedForVisibility(string mode)
        {
            foreach (var rows in new[] { 40, 120 })
            {
                foreach (var strategy in new[] { PlanStrategy.Gj, PlanStrategy.PureSize, PlanStrategy.MinCover })
                {
                    foreach (var bindKey in new[] { true, false })
                    {
                        var db = ExecutionTestUtil.NewDatabase(mode);
                        var t = db.AddTable(new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
                        var s = db.AddTable(Strict(1, 2));
                        DatabaseTestUtil.Stage(db, t, Enumerable.Range(0, rows).Select(i => new[] { (uint)i, (uint)(i % 4) }).ToArray());
                        DatabaseTestUtil.Stage(db, s, [0, 10], [1, 11], [2, 12], [3, 13]);
                        db.MergeAll();

                        // Index t by its value column while every row is live.
                        db.GetTableInfo(t).GetColumnIndex(C(1));
                        db.GetTableInfo(t).GetIndex([C(1), C(0)]);

                        // Move every row of value 3 to value 9 and delete every other row of value 2: their old rows
                        // become tombstones the indexes keep listing (too few for the table to compact, which would start
                        // a new generation and rebuild the indexes). Value 3 is left with tombstones only.
                        DatabaseTestUtil.Stage(db, t, Enumerable.Range(0, rows).Where(i => i % 4 == 3).Select(i => new[] { (uint)i, 9u }).ToArray());
                        DatabaseTestUtil.StageRemove(db, t, Enumerable.Range(0, rows).Where(i => i % 8 == 2).Select(i => new[] { (uint)i }).ToArray());
                        db.MergeAll();
                        Assert.True(db.GetTable(t).HasStaleRows);

                        var log = new List<uint[]>();
                        var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
                        var rsb = db.NewRuleSet();
                        var q = rsb.NewRule();
                        q.Strategy = strategy;
                        var x = q.NewVariable("x");
                        var v = q.NewVariable("v");
                        var w = q.NewVariable("w");

                        // Without the key, no later stage scans t's refined rows, so only the probe's refinement can
                        // drop the tombstones.
                        q.AddAtom(t, [bindKey ? x : QueryEntry.Wildcard(), v]);
                        q.AddAtom(s, [v, w]);
                        var rb = q.Build();
                        if (bindKey)
                            rb.CallExternal(recorder, [x, v, w]);
                        else
                            rb.CallExternal(recorder, [v, w]);

                        rb.Build();
                        using var rs = rsb.Build();
                        db.RunRuleSet(rs);

                        var live = Enumerable.Range(0, rows).Where(i => i % 4 != 3 && i % 8 != 2).ToArray();
                        var expected = bindKey
                            ? ExecutionTestUtil.Distinct(live.Select(i => new[] { (uint)i, (uint)(i % 4), (uint)(10 + i % 4) }))
                            : ExecutionTestUtil.Distinct(live.Select(i => new[] { (uint)(i % 4), (uint)(10 + i % 4) }));
                        Assert.Equal(expected, ExecutionTestUtil.Distinct(log));
                    }
                }
            }
        }

        /// <summary>
        /// A semi-naive header restricting an atom to a range of most of the table probes the database's cached index
        /// clamped to that range, refining for visibility; a range of less than half the table builds a per-node index.
        /// Both must see exactly the live rows in the range.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void ClampedCachedIndexSeesOnlyTheRange(string mode)
        {
            foreach (var bindKey in new[] { true, false })
            {
                var db = ExecutionTestUtil.NewDatabase(mode);
                var t = db.AddTable(new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
                var s = db.AddTable(Strict(1, 2));
                for (uint ts = 0; ts < 4; ts++)
                {
                    DatabaseTestUtil.Stage(db, t, Enumerable.Range(0, 30).Select(i => new[] { ts * 100 + (uint)i, (uint)(i % 3), ts }).ToArray());
                    db.MergeAll();
                }

                DatabaseTestUtil.Stage(db, s, [0, 10], [1, 11], [2, 12]);
                db.GetTableInfo(t).GetColumnIndex(C(1));

                // Value 2 keeps live rows at timestamp 0 only: outside the ranges below, so the index lists it under
                // rows the range excludes and tombstones the range includes.
                var removed = from stamp in new uint[] { 1, 2, 3 } from i in Enumerable.Range(0, 30) where i % 3 == 2 select new[] { stamp * 100 + (uint)i };
                DatabaseTestUtil.StageRemove(db, t, removed.ToArray());
                db.MergeAll();
                Assert.True(db.GetTable(t).HasStaleRows);

                foreach (var since in new uint[] { 1, 3 })
                {
                    var log = new List<uint[]>();
                    var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
                    var rsb = db.NewRuleSet();
                    var q = rsb.NewRule();
                    q.Strategy = PlanStrategy.Gj;
                    var x = q.NewVariable("x");
                    var v = q.NewVariable("v");
                    var w = q.NewVariable("w");
                    q.AddAtom(t, [bindKey ? x : QueryEntry.Wildcard(), v, QueryEntry.Wildcard()], [Constraint.GeConst(C(2), V(since))]);
                    q.AddAtom(s, [v, w]);
                    var rb = q.Build();
                    if (bindKey)
                        rb.CallExternal(recorder, [x, w]);
                    else
                        rb.CallExternal(recorder, [v, w]);

                    rb.Build();
                    using var rs = rsb.Build();
                    db.RunRuleSet(rs);

                    var expected = new List<uint[]>();
                    for (var stamp = since; stamp < 4; stamp++)
                        for (uint i = 0; i < 30; i++)
                            if (i % 3 != 2)
                                expected.Add(bindKey ? [stamp * 100 + i, 10 + i % 3] : [i % 3, 10 + i % 3]);

                    Assert.Equal(ExecutionTestUtil.Distinct(expected), ExecutionTestUtil.Distinct(log));
                }
            }
        }

    }

}
