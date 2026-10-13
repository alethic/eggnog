using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The done condition of colored-contexts spec §17 M10.2, with <c>EGGNOG_COUNTERS</c> (<see cref="ExecutorCounters"/>,
    /// which the suite turns on, <see cref="ExecutorCountersSetup"/>): once a rule's watermark W is at or above every root
    /// stamp, a child's semi-naive run scans no ancestor row of an atom's root rows and builds no index over one. It reads
    /// the root's rows only through the root's shared cached indexes (zero build cost, §14.2) and through the results of
    /// its probes, whatever the root's size.
    /// </summary>
    public class LayeredRunCountersTests
    {

        /// <summary>
        /// A frozen root with a large edge relation <c>e(x, y)</c> and marks <c>q(x)</c>; a child that adds a few edges and
        /// marks of its own.
        /// </summary>
        static (Store Store, TableId E, TableId Q, TableId R, Context Child, int Fresh) Setup(int rootEdges)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var e = store.AddTable(Relation(2), name: "e");
            var q = store.AddTable(Relation(1), name: "q");
            var r = store.AddTable(Relation(2), name: "r");
            var root = store.Root;
            var ids = Enumerable.Range(0, rootEdges / 2).Select(_ => root.NewId()).ToArray();
            var random = new Random(rootEdges);
            root.Write(state =>
            {
                for (var i = 0; i < rootEdges; i++)
                    state.StageInsert(e, [ids[random.Next(ids.Length)], ids[random.Next(ids.Length)], state.Timestamp]);

                for (var i = 0; i < ids.Length; i += 4)
                    state.StageInsert(q, [ids[i], state.Timestamp]);

                return 0;
            });

            root.Freeze();
            var child = root.CreateChild();
            const int Fresh = 10;
            child.Write(state =>
            {
                for (var i = 0; i < Fresh; i++)
                {
                    state.StageInsert(e, [ids[i * 3], ids[i * 5 + 1], state.Timestamp]);
                    state.StageInsert(q, [ids[i * 7 + 2], state.Timestamp]);
                }

                return 0;
            });

            return (store, e, q, r, child, Fresh);
        }

        /// <summary>
        /// Caches <c>e(x, y), q(x) → r(x, y)</c>, planned in the child with <paramref name="strategy"/>.
        /// </summary>
        static CachedPlan Plan(Store store, TableId e, TableId q, TableId r, Context child, PlanStrategy strategy, bool noDecomposition)
        {
            var rsb = child.Database.NewRuleSet();
            var b = rsb.NewRule();
            b.Strategy = strategy;
            b.NoDecomposition = noDecomposition;
            var x = b.NewVariable("x");
            var y = b.NewVariable("y");
            b.AddAtom(e, [x, y, QueryEntry.Wildcard()]);
            b.AddAtom(q, [x, QueryEntry.Wildcard()]);
            var rb = b.Build();
            var ts = rb.ReadCounter(store.ClockCounter);
            rb.Insert(r, [x, y, ts]);
            var rule = rb.Build("e and q");
            using var rs = rsb.Build();
            return rs.BuildCachedPlan(rule);
        }

        /// <summary>
        /// The plans whose stage order follows the atoms' sizes: egglog's default Generic Join order and the size-ordered
        /// Free Join. <see cref="PlanStrategy.MinCover"/> covers with the atom holding the most variables whatever its
        /// size, so its plan scans the old side as a cover by design
        /// (<see cref="MinCoverScansItsChosenCover"/>); that is the plan's choice, not the executor's.
        /// </summary>
        public static IEnumerable<object[]> Plans() =>
            from strategy in new[] { PlanStrategy.Gj, PlanStrategy.PureSize }
            from decomposition in new[] { false, true }
            select new object[] { strategy.ToString(), decomposition };

        [Theory]
        [MemberData(nameof(Plans))]
        public void ASemiNaiveRunAboveEveryRootStampTouchesNoAncestorRootRow(string strategyName, bool noDecomposition)
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            foreach (var rootEdges in new[] { 2_000, 20_000 })
            {
                var (store, e, q, r, child, fresh) = Setup(rootEdges);
                var cached = Plan(store, e, q, r, child, strategy, noDecomposition);
                var w = child.CreatedAt;
                var counts = child.Database.ExecutorCounts!;
                counts.Reset();

                // The semi-naive split at W: e new; or e old and q new.
                var ts = new ColumnId(2);
                var qts = new ColumnId(1);
                var report = LayeredRuleUtil.Run(child, rsb =>
                {
                    rsb.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.GeConst(ts, new Value(w)))]);
                    rsb.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.LtConst(ts, new Value(w))), (new AtomId(1), Constraint.GeConst(qts, new Value(w)))]);
                });

                var context = $"{strategy}, noDecomposition {noDecomposition}, {rootEdges} root edges: {counts}";
                Assert.True(report.NumMatches("e and q") > 0, context);
                Assert.True(counts.AncestorCachedIndexPieces > 0, context);
                Assert.True(counts.AncestorRowsScanned == 0, context);
                Assert.True(counts.AncestorDynamicIndexRows == 0, context);

                // The work that does reach ancestor rows is bounded by what the child probed, not by the root.
                Assert.True(counts.AncestorRefinedRowsScanned + counts.AncestorRefinedIndexRows < 400 * fresh, context);
                child.CheckInvariants();

                // Without the watermark the run reads the root's rows: the counters do see ancestor work.
                counts.Reset();
                LayeredRuleUtil.Run(child, rsb => rsb.AddRuleFromCachedPlan(cached));
                Assert.True(counts.AncestorRowsScanned + counts.AncestorDynamicIndexRows + counts.AncestorCachedIndexPieces > 0, $"{context} (full run)");
            }
        }

        [Fact]
        public void MinCoverScansItsChosenCover()
        {
            Assert.SkipUnless(ExecutorCounters.Enabled, "The executor's counters are off (EGGNOG_COUNTERS=0).");
            var (store, e, q, r, child, _) = Setup(2_000);
            var cached = Plan(store, e, q, r, child, PlanStrategy.MinCover, noDecomposition: true);
            var w = child.CreatedAt;
            var counts = child.Database.ExecutorCounts!;
            counts.Reset();
            var oldEdges = store.Schema[e]!.Root.Count;
            LayeredRuleUtil.Run(child, rsb => rsb.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.LtConst(new ColumnId(2), new Value(w))), (new AtomId(1), Constraint.GeConst(new ColumnId(1), new Value(w)))]));

            // The cover is e, all of whose rows are old: the scan is the plan's, at most the old side once.
            Assert.InRange(counts.AncestorRowsScanned, 1, oldEdges);
            Assert.Equal(0, counts.AncestorDynamicIndexRows);
        }

    }

}
