using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The staleness and timestamp guards of a rule run in a child (colored-contexts spec §9.1, I3, I19, I21, I29): a rule
    /// set whose headers were evaluated before a change anywhere on the chain is refused even once the child is current
    /// again, a run or merge below the child's latest epoch is refused before anything runs, and a root keeps egglog's
    /// rule (a stale rule set runs). No refusal faults the child.
    /// </summary>
    public class ChildRunStalenessTests
    {

        /// <summary>
        /// A store with <c>f(x)</c> holding five ids at the root, an empty <c>g(x)</c>, and a child of the root, which is
        /// left writable.
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

        [Fact]
        public void ARunBelowTheChildsLatestEpochIsRefused()
        {
            var (store, f, g, _, child) = Setup();
            using (child)
            {
                // Two writes: the child's latest epoch is now above its creation, the floor its rule runs knew of before.
                Insert(child, f, child.NewId(), 0);
                Insert(child, f, child.NewId(), 0);
                using var rs = Build(store, f, g, child);
                Assert.Throws<ArgumentOutOfRangeException>(() => child.Database.RunRuleSet(rs, ReportLevel.TimeOnly, child.CreatedAt));
                Assert.False(child.IsFaulted);
                Assert.True(child.IsClean);
                Assert.Empty(LayeredRuleUtil.Visible(child, g));

                // The same rule set runs at the clock, and every row it writes carries that epoch (I3, I19).
                var epoch = store.ReadClock();
                child.Database.RunRuleSet(rs, ReportLevel.TimeOnly, epoch);
                Assert.Equal(7, LayeredRuleUtil.Visible(child, g).Count);
                Assert.All(ScanVisible(child.GetView(g)), row => Assert.Equal(epoch, row.Cells[1]));
                child.CheckInvariants();
            }
        }

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
