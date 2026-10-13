using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The staleness and timestamp guards of a rule run in a child: a rule set whose headers were evaluated before a
    /// change anywhere on the chain is refused even once the child is current again, since its headers and root nodes
    /// must come from the views the search reads; a run or merge below the child's latest epoch is refused before
    /// anything runs, so new rows are never stamped below a watermark already committed; and a root keeps egglog's rule
    /// (a stale rule set runs). No refusal faults the child.
    /// </summary>
    public class ChildRunStalenessTests
    {

        /// <summary>
        /// A store with <c>f(x)</c> holding five ids at the root, an empty <c>g(x)</c>, and a child of the root, which
        /// is left writable.
        /// </summary>
        static (Store Store, TableId F, TableId G, Context Root, Context Child) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(Relation(1), name: "f");
            var g = store.AddTable(Relation(1), name: "g");
            var root = store.Root;
            var ids = Enumerable.Range(0, 5).Select(_ => root.NewId()).ToArray();
            root.Write(state =>
            {
                foreach (var id in ids)
                    state.StageInsert(f, [id, state.Timestamp]);

                return 0;
            });

            return (store, f, g, root, root.CreateChild());
        }

        /// <summary>
        /// Builds <c>f(x) → g(x)</c> in <paramref name="context"/>.
        /// </summary>
        static RuleSet Build(Store store, TableId f, TableId g, Context context)
        {
            var rsb = context.Database.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            q.AddAtom(f, [x, QueryEntry.Wildcard()]);
            var rb = q.Build();
            var ts = rb.ReadCounter(store.ClockCounter);
            rb.Insert(g, [x, ts]);
            rb.Build("f to g");
            return rsb.Build();
        }

        /// <summary>
        /// A rule set built before the parent appended a row is refused after the child catches up, although the
        /// child's views are current again; built again, it runs over the new row.
        /// </summary>
        [Fact]
        public void ARuleSetBuiltBeforeTheChildCaughtUpWithItsParentIsRefused()
        {
            var (store, f, g, root, child) = Setup();
            using (child)
            {
                using var rs = Build(store, f, g, child);

                // The parent appends a row and the child catches up: its views are current again, but the rule set's
                // headers were evaluated before the row existed.
                Insert(root, f, root.NewId(), 0);
                child.Rebuild();
                Assert.True(child.IsClean);
                Assert.Throws<InvalidOperationException>(() => child.Database.RunRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock()));
                Assert.False(child.IsFaulted);
                Assert.Empty(LayeredRuleUtil.Visible(child, g));

                using var again = Build(store, f, g, child);
                child.Database.RunRuleSet(again, ReportLevel.TimeOnly, store.ReadClock());
                Assert.Equal(6, LayeredRuleUtil.Visible(child, g).Count);
                child.CheckInvariants();
            }
        }

        /// <summary>
        /// A run at an epoch below the child's latest is refused with nothing written; at the clock it runs, and every
        /// row it writes carries that epoch.
        /// </summary>
        [Fact]
        public void ARunBelowTheChildsLatestEpochIsRefused()
        {
            var (store, f, g, _, child) = Setup();
            using (child)
            {
                // Two writes: the child's latest epoch is above its creation epoch, so a run at the creation epoch is
                // below it.
                Insert(child, f, child.NewId(), 0);
                Insert(child, f, child.NewId(), 0);
                using var rs = Build(store, f, g, child);
                Assert.Throws<ArgumentOutOfRangeException>(() => child.Database.RunRuleSet(rs, ReportLevel.TimeOnly, child.CreatedAt));
                Assert.False(child.IsFaulted);
                Assert.True(child.IsClean);
                Assert.Empty(LayeredRuleUtil.Visible(child, g));

                // The same rule set runs at the clock, and every row it writes carries that epoch (one run's rows carry
                // one epoch, and a layer's timestamps never decrease).
                var epoch = store.ReadClock();
                child.Database.RunRuleSet(rs, ReportLevel.TimeOnly, epoch);
                Assert.Equal(7, LayeredRuleUtil.Visible(child, g).Count);
                Assert.All(ScanVisible(child.GetView(g)), row => Assert.Equal(epoch, row.Cells[1]));
                child.CheckInvariants();
            }
        }

        /// <summary>
        /// Merging an executed rule set at an epoch below the one it ran at is refused without faulting the child; at
        /// its own epoch the merge lands.
        /// </summary>
        [Fact]
        public void AMergeBelowItsRunsTimestampIsRefused()
        {
            var (store, f, g, _, child) = Setup();
            using (child)
            {
                Insert(child, f, child.NewId(), 0);
                using var rs = Build(store, f, g, child);
                var epoch = store.ReadClock();
                var executed = child.Database.ExecuteRuleSet(rs, ReportLevel.TimeOnly, epoch);
                Assert.Throws<ArgumentOutOfRangeException>(() => child.Database.MergeRuleSet(rs, executed, epoch - 1));
                Assert.False(child.IsFaulted);

                child.Database.MergeRuleSet(rs, executed, epoch);
                Assert.Equal(6, LayeredRuleUtil.Visible(child, g).Count);
                child.CheckInvariants();
            }
        }

        /// <summary>
        /// A root runs a rule set built before its last change, as egglog does, and finds the new row.
        /// </summary>
        [Fact]
        public void ARootRunsAStaleRuleSetAsEgglogDoes()
        {
            var (store, f, g, root, child) = Setup();
            child.Dispose();

            // egglog's rule at a root: header subsets are not checked against later merges; build right before running.
            using var rs = Build(store, f, g, root);
            Assert.Equal(0, rs.ViewStamp);
            Insert(root, f, root.NewId(), 0);
            root.Database.RunRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock());
            Assert.Equal(6, LayeredRuleUtil.Visible(root, g).Count);
        }

    }

}
