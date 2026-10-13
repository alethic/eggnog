using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the layered-subset members of <see cref="TableView"/> and <see cref="LayerView"/>:
    /// <see cref="TableView.AllRows"/> (the rows a layered run starts from) and <see cref="TableView.AnyVisible"/> (the
    /// existence test of a zero-variable atom, which needs a visible row, not merely a nonempty table), over views with
    /// tombstones, hidden rows and absent layers.
    /// </summary>
    public class TableViewRowsTests
    {

        /// <summary>
        /// A chain root, child, grandchild over <c>f(k) → v</c>: the root holds <c>f(1..12)</c> and deletes <c>f(5)</c>
        /// (a tombstone; the root is not frozen, so its freeze does not compact it away), the child raises <c>f(2)</c> and
        /// <c>f(3)</c> (hiding the root's rows) and adds <c>f(20)</c>, and the grandchild raises <c>f(20)</c> (hiding the
        /// child's row).
        /// </summary>
        static (Store Store, TableId F, TableId G, Context Child, Context Grandchild) Chain()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(LayeredTestUtil.MaxFunction(1), name: "f");
            var g = store.AddTable(LayeredTestUtil.MaxFunction(1), name: "g");
            for (var k = 1u; k <= 12; k++)
                LayeredTestUtil.Insert(store.Root, f, k, k, 0);

            LayeredTestUtil.Remove(store.Root, f, 5);
            var child = store.Root.CreateChild();
            LayeredTestUtil.Insert(child, f, 2, 50, 0);
            LayeredTestUtil.Insert(child, f, 3, 50, 0);
            LayeredTestUtil.Insert(child, f, 20, 1, 0);
            var grandchild = child.CreateChild();
            LayeredTestUtil.Insert(grandchild, f, 20, 9, 0);
            return (store, f, g, child, grandchild);
        }

        /// <summary>
        /// Whether row <paramref name="row"/> of a layer is visible to the view: live and not hidden.
        /// </summary>
        static bool IsVisible(LayerView layer, int row) =>
            layer.Sorted!.Rows.Data[row * layer.Table.Arity] != Value.StaleRep && layer.Hidden.Contains(row) == false;

        [Fact]
        public void AllRowsListsEveryPhysicalRowOfEveryPresentLayer()
        {
            var (store, f, g, child, grandchild) = Chain();
            var pool = new SubsetArrayPool(3);
            var view = grandchild.GetView(f);
            Assert.Equal(0b111u, view.PresentMask);
            var all = view.AllRows(pool);
            Assert.True(all.IsMultiLayer);
            Assert.Equal(0b111u, all.LayerMask);
            for (var j = 0; j < 3; j++)
            {
                var expected = view.Layers[j]!.Table.All();
                Assert.Equal(expected, all.GetLayer(j).ToOwned());
            }

            Assert.Equal(view.Layers.Sum(l => l!.Sorted!.Rows.Count), all.Size);
            all.Dispose(pool);
            Assert.Equal(1, pool.Count);

            // The root's view: one layer, the root path.
            var rootView = store.Schema.RootView(f);
            var rootRows = rootView.AllRows(pool);
            Assert.False(rootRows.IsMultiLayer);
            Assert.Equal(0, rootRows.SingleLayer);
            Assert.Equal(Subset.Dense(0, rootView.Layers[0]!.Sorted!.Rows.Count), rootRows.SingleRows.ToOwned());
            rootRows.Dispose(pool);

            // A table nobody wrote: present layers without rows give Empty.
            var empty = grandchild.GetView(g).AllRows(pool);
            Assert.True(empty.IsEmpty);
            Assert.False(empty.IsMultiLayer);

            // Without a pool the array is allocated.
            var unpooled = view.AllRows(null);
            Assert.Equal(0b111u, unpooled.LayerMask);
            unpooled.Dispose(null);
            grandchild.Dispose();
            child.Dispose();
        }

        [Fact]
        public void AnyVisibleFindsAVisibleRowOrNone()
        {
            var (store, f, _, child, grandchild) = Chain();
            var view = grandchild.GetView(f);
            var random = new Random(4);
            for (var j = 0; j < view.LayerCount; j++)
            {
                var layer = view.Layers[j]!;
                var count = layer.Sorted!.Rows.Count;
                Assert.Equal(j < 2, layer.HasInvisible);
                for (var trial = 0; trial < 300; trial++)
                {
                    var rows = OffsetTestUtil.RandomSorted(random, random.Next(0, Math.Min(count, 4) + 1), count, duplicates: false);
                    var expected = rows.Any(r => IsVisible(layer, r));
                    Assert.Equal(expected, view.AnyVisible(j, SubsetRef.Sparse(rows.ToArray(), 0, rows.Count)));
                    if (rows.Count > 0)
                    {
                        var start = rows[0];
                        var end = rows[^1] + 1;
                        Assert.Equal(Enumerable.Range(start, end - start).Any(r => IsVisible(layer, r)), view.AnyVisible(j, SubsetRef.Dense(new OffsetRange(start, end))));
                    }
                }
            }

            // Only hidden rows, or only a tombstone, in the root layer: nothing visible.
            var root = view.Layers[0]!;
            var hidden = Enumerable.Range(0, root.Sorted!.Rows.Count).Where(r => root.Hidden.Contains(r)).ToArray();
            var stale = Enumerable.Range(0, root.Sorted.Rows.Count).Where(r => root.Sorted.Rows.Data[r * root.Table.Arity] == Value.StaleRep).ToArray();
            Assert.Equal(2, hidden.Length);
            Assert.Single(stale);
            Assert.False(view.AnyVisible(0, SubsetRef.Sparse(hidden, 0, hidden.Length)));
            Assert.False(view.AnyVisible(0, SubsetRef.Sparse(stale, 0, stale.Length)));
            var both = hidden.Concat(stale).OrderBy(r => r).ToArray();
            Assert.False(view.AnyVisible(0, SubsetRef.Sparse(both, 0, both.Length)));

            // The child's f(20) is hidden by the grandchild, whose own copy is visible; the child's f(2) and f(3) are not.
            var childLayer = view.Layers[1]!;
            var childRows = childLayer.Sorted!.Rows;
            var twenty = Enumerable.Range(0, childRows.Count).Single(r => childRows.Data[r * childLayer.Table.Arity] == 20);
            Assert.False(view.AnyVisible(1, SubsetRef.Dense(new OffsetRange(twenty, twenty + 1))));
            Assert.True(view.AnyVisible(1, childLayer.Table.All().AsRef()));
            Assert.True(view.AnyVisible(2, view.Layers[2]!.Table.All().AsRef()));

            // Empty subsets, absent and out-of-range layers have nothing visible; a subset past the rows is refused.
            Assert.False(view.AnyVisible(0, default));
            Assert.False(view.AnyVisible(-1, SubsetRef.Dense(new OffsetRange(0, 1))));
            Assert.False(view.AnyVisible(3, SubsetRef.Dense(new OffsetRange(0, 1))));
            Assert.False(store.Schema.RootView(f).AnyVisible(1, SubsetRef.Dense(new OffsetRange(0, 1))));
            Assert.Throws<ArgumentOutOfRangeException>(() => view.AnyVisible(0, SubsetRef.Dense(new OffsetRange(0, root.Sorted.Rows.Count + 1))));
            grandchild.Dispose();
            child.Dispose();
        }

    }

}
