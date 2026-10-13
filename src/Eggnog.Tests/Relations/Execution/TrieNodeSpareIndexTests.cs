using System;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A trie node keeps each per-column index it built as a spare when its caches are reset, and refills that index
    /// in place for its next rows, so recycled nodes probed through their own column index allocate nothing.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class TrieNodeSpareIndexTests
    {

        static SortedWritesTable Table() => TableTestUtil.FillTable(Enumerable.Range(0, 20).Select(i => new[] { (uint)i, (uint)(i % 3) }), 1, null, ClosureMerge.TakeNew);

        [Fact]
        public void ARecycledNodeRefillsItsSpareIndex()
        {
            var table = Table();
            var pool = new TrieNodePool(false);
            var node = pool.Rent(Subset.Dense(5, 15));
            var index = node.GetCachedIndex(new ColumnId(1), table);
            node.Release(pool);

            var again = pool.Rent(Subset.Dense(0, 7));
            Assert.Same(node, again);
            var rebuilt = again.GetCachedIndex(new ColumnId(1), table);
            Assert.Same(index, rebuilt);
            Assert.Equal(3, rebuilt.Count);
            Assert.True(rebuilt.TryGetSubset(0, out var rows));
            Assert.Equal([0, 3, 6], ExecutionTestUtil.Ids(rows));
            Assert.True(rebuilt.TryGetSubset(2, out rows));
            Assert.Equal([2, 5], ExecutionTestUtil.Ids(rows));
            Assert.Same(rebuilt, again.GetCachedIndex(new ColumnId(1), table));
        }

        [Fact]
        public void SparesArePerColumn()
        {
            var table = Table();
            var pool = new TrieNodePool(false);
            var node = pool.Rent(Subset.Dense(0, 20));
            var byValue = node.GetCachedIndex(new ColumnId(1), table);
            node.ResetDense(new OffsetRange(10, 20), pool);

            // Column 0 had no index, so it gets a new one; column 1 gets its spare back, over the new rows.
            var byKey = node.GetCachedIndex(new ColumnId(0), table);
            Assert.NotSame(byValue, byKey);
            Assert.Equal(10, byKey.Count);
            var again = node.GetCachedIndex(new ColumnId(1), table);
            Assert.Same(byValue, again);
            Assert.True(again.TryGetSubset(1, out var rows));
            Assert.Equal([10, 13, 16, 19], ExecutionTestUtil.Ids(rows));
        }

        [Fact]
        public void ARecycledConcurrentNodeBuildsOneIndexForAllThreads()
        {
            var table = Table();
            var pool = new TrieNodePool(true);
            var node = pool.Rent(Subset.Dense(5, 15));
            node.GetCachedIndex(new ColumnId(1), table);
            node.Release(pool);

            for (var round = 0; round < 20; round++)
            {
                var again = pool.Rent(Subset.Dense(0, 20));
                var results = new ColumnIndex[16];
                Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => results[i] = again.GetCachedIndex(new ColumnId(1), table));
                Assert.All(results, x => Assert.Same(results[0], x));
                Assert.True(results[0].TryGetSubset(0, out var rows));
                Assert.Equal(Enumerable.Range(0, 7).Select(i => 3 * i).ToArray(), ExecutionTestUtil.Ids(rows));
                again.Release(pool);
            }
        }

        [Fact]
        public void PooledNodesProbedThroughTheirColumnIndexStopAllocating()
        {
            var table = TableTestUtil.FillTable(Enumerable.Range(0, 400).Select(i => new[] { (uint)i, (uint)(i % 7) }), 1, null, ClosureMerge.TakeNew);
            var pool = new TrieNodePool(false);
            var nodes = new TrieNode[64];
            for (var round = 0; round < 10; round++)
                Cycle(pool, table, nodes);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var round = 0; round < 100; round++)
                Cycle(pool, table, nodes);

            var perNode = (GC.GetAllocatedBytesForCurrentThread() - before) / (100.0 * nodes.Length);
            Assert.True(perNode < 4, $"{perNode:0.0} bytes per node");

            // Many nodes live at once, each with its own index over a different subset, as at one join position.
            static void Cycle(TrieNodePool pool, Table table, TrieNode[] nodes)
            {
                for (var i = 0; i < nodes.Length; i++)
                {
                    nodes[i] = pool.Rent(Subset.Dense(4 * i, 4 * i + 13));
                    nodes[i].GetCachedIndex(new ColumnId(1), table);
                }

                for (var i = 0; i < nodes.Length; i++)
                    nodes[i].Release(pool);
            }
        }

    }

}
