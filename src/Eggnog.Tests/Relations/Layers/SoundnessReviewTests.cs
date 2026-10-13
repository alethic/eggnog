using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests from M9.3 (soundness lens), kept as regressions: each exposed a behaviour that broke context = copy, a
    /// stated deviation or the spec's interim. With the child's rebuild (M9.4) the interim refusals are gone, and the
    /// child catches up instead.
    /// </summary>
    public class SoundnessReviewTests
    {

        /// <summary>
        /// A union-find import over the child's own rows: the root unions a and b after the child wrote g(b); no root table
        /// changed. The child's rebuild rewrites its row (step 3, §7.3): a clean child that answered g(b) and missed g(a)
        /// would break I13 and context = copy.
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

            child.Rebuild();
            Assert.True(child.IsClean);
            Assert.Equal(a, child.Find(b));

            // A copy (the root's rows plus the child's write, replayed with the union) holds g(a) = 1 and no g(b).
            Assert.NotNull(Row(child, g, a));
            Assert.Null(Row(child, g, b));
        }

        /// <summary>
        /// The parent writes a row while its child lives and then freezes (edge case 22: allowed, the child catches up
        /// once). In M9.3 the child's rebuild was refused (row catch-up arrived in M9.4); now the child catches the row up,
        /// through a direct rebuild or through its own freeze, and neither faults it.
        /// </summary>
        [Fact]
        public void AChildCatchesUpWithAParentThatWroteAndFroze()
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

            // The row is visible as it is (§7.2 branch c): nothing is copied, so the rebuild changes nothing of the child's.
            Assert.False(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.True(child.IsClean);
            Assert.Equal(1u, Row(child, f, a)![1]);

            child.Freeze();
            Assert.False(child.IsFaulted);
            Assert.True(child.IsFrozen);
            Assert.Equal(1u, Row(child, f, a)![1]);
            Assert.Equal(0, child.Tables!.Statistics.RowsCopied);
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
