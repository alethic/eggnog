using System;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Trie nodes over rows of several layers. A node holds a <see cref="LayeredSubset"/>; its per-column indexes are
    /// per (layer, column) and see only what each layer's view sees; its child caches are keyed by a slot that names
    /// one row set (selector 0 for every layer, <c>l + 1</c> for layer <c>l</c>), so a cached child is a function of
    /// the node's rows, the slot and the key; a single-layer node, every root node among them, uses the slots of an
    /// unlayered node: the column alone.
    /// </summary>
    public class LayeredTrieNodeTests
    {

        /// <summary>
        /// A table <c>(k; v)</c> of 20 rows with <c>v = k mod 4</c>, and its view with the rows in <paramref name="hidden"/>
        /// hidden.
        /// </summary>
        static (SortedWritesTable Table, LayerView View) Layer(int depth, uint offset, params int[] hidden)
        {
            var table = TableTestUtil.FillTable(Enumerable.Range(0, 20).Select(i => new[] { offset + (uint)i, (uint)(i % 4) }), 1, null, ClosureMerge.TakeNew);
            HiddenSet? set = null;
            if (hidden.Length > 0)
            {
                set = new HiddenSet(table.Version.Major);
                foreach (var row in hidden)
                    set.Add(row, table.Rows.Count);

                set.Seal(table.Rows.Count);
            }

            return (table, new LayerView(depth, new TableInfo("t", table, ParallelHeuristics.Serial), new HiddenRows(null, set), false));
        }

        static LayeredSubset Rows(params (int Layer, Subset Rows)[] layers)
        {
            var array = new Subset[3];
            foreach (var (layer, rows) in layers)
                array[layer] = rows;

            return LayeredSubset.FromLayers(array, null);
        }

        [Fact]
        public void AMultiLayerNodeReportsItsLayers()
        {
            var pool = new TrieNodePool(false, 3);
            var node = pool.Rent(Rows((0, Subset.Dense(0, 5)), (2, Subset.Dense(3, 10))));
            Assert.True(node.IsMultiLayer);
            Assert.Equal(-1, node.SingleLayer);
            Assert.Equal(0b101u, node.LayerMask);
            Assert.Equal(12, node.Size);
            Assert.Equal([0, 1, 2, 3, 4], ExecutionTestUtil.Ids(node.GetLayerRows(0)));
            Assert.True(node.GetLayerRows(1).IsEmpty);
            Assert.Throws<InvalidOperationException>(() => node.Rows);

            var single = pool.Rent(2, Subset.Dense(1, 4));
            Assert.False(single.IsMultiLayer);
            Assert.Equal(2, single.SingleLayer);
            Assert.Equal(0b100u, single.LayerMask);
            Assert.Equal([1, 2, 3], ExecutionTestUtil.Ids(single.Rows));
        }

        [Fact]
        public void IndexesArePerLayerAndSeeOnlyVisibleRows()
        {
            var (_, low) = Layer(0, 0, 0, 4, 8);
            var (_, high) = Layer(2, 100);
            var pool = new TrieNodePool(false, 3);
            var node = pool.Rent(Rows((0, Subset.Dense(0, 20)), (2, Subset.Dense(0, 20))));

            var lowIndex = node.GetCachedIndex(C(1), 0, low);
            var highIndex = node.GetCachedIndex(C(1), 2, high);
            Assert.NotSame(lowIndex, highIndex);
            Assert.Same(lowIndex, node.GetCachedIndex(C(1), 0, low));
            Assert.Same(highIndex, node.GetCachedIndex(C(1), 2, high, out var built));
            Assert.False(built);

            // Value 0 is held by rows 0, 4, 8, 12, 16 of each layer; the low layer's view hides 0, 4 and 8.
            Assert.True(lowIndex.TryGetSubset(0, out var lowRows));
            Assert.Equal([12, 16], ExecutionTestUtil.Ids(lowRows));
            Assert.True(highIndex.TryGetSubset(0, out var highRows));
            Assert.Equal([0, 4, 8, 12, 16], ExecutionTestUtil.Ids(highRows));
        }

        [Fact]
        public void ASingleLayerNodeKeepsTodaysSlots()
        {
            var (table, view) = Layer(1, 0);
            var pool = new TrieNodePool(false, 2);
            var node = pool.Rent(1, Subset.Dense(0, 20));
            var index = node.GetCachedIndex(C(1), 1, view);

            // The column is the slot: the root form, which hides nothing, finds the same index.
            Assert.Same(index, node.GetCachedIndex(C(1), table));
            Assert.Equal(1, TrieNode.ChildSlot(1, 0, table.Arity));
            Assert.Equal(5, TrieNode.ChildSlot(1, 2, table.Arity));
        }

        [Fact]
        public void ChildSlotsNameOneRowSetEach()
        {
            var (table, _) = Layer(0, 0);
            var pool = new TrieNodePool(false, 3);
            var node = pool.Rent(Rows((0, Subset.Dense(0, 10)), (1, Subset.Dense(0, 10))));
            var arity = table.Arity;

            // A probed refinement (every layer) and a driver's refinement per layer, all for value 3 of column 1.
            var all = node.AddChild(TrieNode.ChildSlot(1, 0, arity), 3, pool.Rent(Rows((0, Subset.Dense(3, 4)), (1, Subset.Dense(3, 4)))), arity, pool);
            var layer0 = node.AddChild(TrieNode.ChildSlot(1, 1, arity), 3, pool.Rent(0, Subset.Dense(3, 4)), arity, pool);
            var layer1 = node.AddChild(TrieNode.ChildSlot(1, 2, arity), 3, pool.Rent(1, Subset.Dense(3, 4)), arity, pool);
            Assert.True(node.TryGetChild(TrieNode.ChildSlot(1, 0, arity), 3, out var found));
            Assert.Same(all, found);
            Assert.True(node.TryGetChild(TrieNode.ChildSlot(1, 1, arity), 3, out found));
            Assert.Same(layer0, found);
            Assert.True(node.TryGetChild(TrieNode.ChildSlot(1, 2, arity), 3, out found));
            Assert.Same(layer1, found);
            Assert.Equal(2, all.Size);
            Assert.Equal(0, layer0.SingleLayer);
            Assert.Equal(1, layer1.SingleLayer);
            Assert.False(node.TryGetChild(TrieNode.ChildSlot(1, 0, arity), 4, out _));
        }

        [Fact]
        public void IntersectWorksLayerByLayer()
        {
            var arrays = new SubsetArrayPool(3);
            var pool = new TrieNodePool(false, 3);
            var node = pool.Rent(Rows((0, Subset.Dense(0, 10)), (1, Subset.Dense(0, 10)), (2, Subset.Dense(0, 10))));
            var header = Rows((0, Subset.Dense(5, 20)), (2, Subset.Dense(0, 2)));
            node.Intersect(in header, arrays);
            Assert.Equal(0b101u, node.LayerMask);
            Assert.Equal([5, 6, 7, 8, 9], ExecutionTestUtil.Ids(node.GetLayerRows(0)));
            Assert.Equal([0, 1], ExecutionTestUtil.Ids(node.GetLayerRows(2)));

            // A root's single-layer node and header intersect as plain subsets.
            var root = pool.Rent(Subset.Dense(0, 10));
            var rootHeader = LayeredSubset.Single(0, Subset.Dense(4, 6));
            root.Intersect(in rootHeader, null);
            Assert.Equal([4, 5], ExecutionTestUtil.Ids(root.Rows));
            header.Dispose(null);
            rootHeader.Dispose(null);
        }

        [Fact]
        public void ResetAndReleaseReturnPerLayerArrays()
        {
            var pool = new TrieNodePool(false, 3);
            var arrays = pool.Layers!;
            var node = pool.Rent(LayeredSubset.FromLayers(Rent(arrays, (0, Subset.Dense(0, 4)), (2, Subset.Dense(0, 4))), arrays));
            var (_, view) = Layer(0, 0);
            node.GetCachedIndex(C(1), 0, view);

            // Reused as a position's scratch node: the caches reset and the rows become one layer's dense range.
            var before = arrays.Count;
            node.ResetDense(2, new OffsetRange(1, 3), pool);
            Assert.Equal(before + 1, arrays.Count);
            Assert.False(node.IsMultiLayer);
            Assert.Equal(2, node.SingleLayer);
            Assert.Equal([1, 2], ExecutionTestUtil.Ids(node.Rows));

            var other = pool.Rent(LayeredSubset.FromLayers(Rent(arrays, (0, Subset.Dense(0, 4)), (1, Subset.Dense(0, 4))), arrays));
            before = arrays.Count;
            other.Release(pool);
            Assert.Equal(before + 1, arrays.Count);
            Assert.Same(other, pool.Rent(Subset.Dense(0, 1)));
        }

        [Fact]
        public void AConcurrentNodePublishesPerLayerIndexesOnce()
        {
            var (_, low) = Layer(0, 0);
            var (_, high) = Layer(1, 100);
            var pool = new TrieNodePool(true, 2);
            var node = pool.Rent(Rows((0, Subset.Dense(0, 20)), (1, Subset.Dense(0, 20))));
            var results = new ColumnIndex[16];
            Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => results[i] = node.GetCachedIndex(C(1), i % 2, i % 2 == 0 ? low : high));
            Assert.All(results.Where((_, i) => i % 2 == 0), r => Assert.Same(results[0], r));
            Assert.All(results.Where((_, i) => i % 2 == 1), r => Assert.Same(results[1], r));
            Assert.NotSame(results[0], results[1]);
        }

        static Subset[] Rent(SubsetArrayPool arrays, params (int Layer, Subset Rows)[] layers)
        {
            var array = arrays.Rent();
            foreach (var (layer, rows) in layers)
                array[layer] = rows;

            return array;
        }

    }

}
