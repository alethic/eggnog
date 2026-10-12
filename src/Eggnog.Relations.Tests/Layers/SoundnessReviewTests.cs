using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests for M9.3 (soundness lens): each exposes a behaviour that breaks context = copy, a stated deviation or
    /// the spec's interim. They are expected to fail until the defect is fixed.
    /// </summary>
    public class SoundnessReviewTests
    {

        /// <summary>
        /// The refined interim refusal (Rebuild throws only when an ancestor's layer version moved) admits a union-find
        /// import over the child's own rows: the root unions a and b after the child wrote g(b); no root table changed, so
        /// the child commits its cursors and reports clean with an own row naming b, which is no longer canonical in it.
        /// The spec's interim refuses (NeedsCatchUp); M9.4 rewrites the row (step 3). Either is sound; a clean child that
        /// answers g(b) and misses g(a) is not (I13, context = copy).
        /// </summary>
        [Fact]
        public void AParentUnionOverAnOwnRowNeverLeavesACleanStaleChild()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(MaxFunction(1), name: "g");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var child = root.CreateChild();
            Insert(child, g, b, 1, 0);

            // The root (not frozen, same domain) unions a and b: b loses. No root row mentions either id.
            root.Union(a, b);

            try
            {
                child.Rebuild();
            }
            catch (NotSupportedException)
            {
                // The spec's interim: refused before anything changed.
                Assert.False(child.IsFaulted);
                return;
            }

            Assert.True(child.IsClean);
            Assert.Equal(a, child.Find(b));

            // A copy (the root's rows plus the child's write, replayed with the union) holds g(a) = 1 and no g(b).
            Assert.NotNull(Row(child, g, a));
            Assert.Null(Row(child, g, b));
        }

        /// <summary>
        /// The deviation states that the interim row catch-up refusal "throws before anything changes, so the child is not
        /// faulted". Through <see cref="Context.Freeze"/> it does fault: the parent wrote a row and froze (allowed with a live
        /// child), and the child's freeze runs its rebuild inside the freeze's faulting catch. A direct Rebuild of the same
        /// child refuses without faulting, so the same refusal leaves the child usable or dead depending on the entry point.
        /// </summary>
        [Fact]
        public void TheInterimRefusalDoesNotFaultAChildThroughFreeze()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            root.Freeze();
            var parent = root.CreateChild();
            var child = parent.CreateChild();

            // The parent writes while its child lives, then freezes (edge case 22: allowed, the child catches up once).
            Insert(parent, f, a, 1, 0);
            parent.Freeze();

            Assert.Throws<NotSupportedException>(() => child.Rebuild());
            Assert.False(child.IsFaulted);

            Assert.Throws<NotSupportedException>(() => child.Freeze());
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// <see cref="ExecutionState.VisibleRowCount"/> of the union-find in a child returns the child's journal count, which
        /// omits every displacement its ancestors made before it was created. §6.5 routes a child through
        /// <c>ViewFor(t).VisibleCount</c>, which refuses the union-find (X23); a copy of the context reports the
        /// displacements it sees. The child reports 0 where its root, whose rows it sees, reports 1.
        /// </summary>
        [Fact]
        public void AChildsUnionFindRowCountIsNotItsJournalCount()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            root.Union(a, b);
            root.Rebuild();
            root.Freeze();
            var child = root.CreateChild();
            Assert.Equal(a, child.Find(b));

            var rootCount = root.Database.CreateExecutionState().VisibleRowCount(store.UnionFindTable);
            Assert.Equal(1, rootCount);

            long childCount;
            try
            {
                childCount = child.Database.CreateExecutionState().VisibleRowCount(store.UnionFindTable);
            }
            catch (NotSupportedException)
            {
                // §6.5 / X23: the union-find has no view in a child.
                return;
            }

            Assert.Equal(rootCount, childCount);
        }

    }

}
