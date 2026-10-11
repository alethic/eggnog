using System;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// egglog's <c>TrieNode</c> caches (core-relations/src/free_join/execute.rs:662-750) and the node ownership that
    /// replaces its reference counting.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class TrieNodeTests
    {

        static SortedWritesTable Table() => TableTestUtil.FillTable(Enumerable.Range(0, 20).Select(i => new[] { (uint)i, (uint)(i % 3) }), 1, null, ClosureMerge.TakeNew);

        [Fact]
        public void ColumnIndexIsBuiltOnceOverTheNodesRows()
        {
            var table = Table();
            var pool = new TrieNodePool(false);
            var node = pool.Rent(Subset.Dense(5, 15));
            var index = node.GetCachedIndex(new ColumnId(1), table);
            Assert.Same(index, node.GetCachedIndex(new ColumnId(1), table));
            Assert.NotSame(index, node.GetCachedIndex(new ColumnId(0), table));
            Assert.Equal(3, index.Count);
            Assert.True(index.TryGetSubset(0, out var rows));
            Assert.Equal([6, 9, 12], ExecutionTestUtil.Ids(rows));
        }

        [Fact]
        public void ChildrenAreCachedPerColumnAndValue()
        {
            var table = Table();
            var pool = new TrieNodePool(false);
            var node = pool.Rent(table.All());
            Assert.False(node.TryGetChild(1, 2, out _));
            var child = pool.Rent(Subset.Dense(2, 3));
            Assert.Same(child, node.AddChild(1, 2, child, table.Arity, pool));
            Assert.True(node.TryGetChild(1, 2, out var found));
            Assert.Same(child, found);
            Assert.False(node.TryGetChild(0, 2, out _));
            Assert.False(node.TryGetChild(1, 3, out _));
        }

        [Fact]
        public void ConcurrentNodesKeepTheFirstChild()
        {
            var table = Table();
            var pool = new TrieNodePool(true);
            var node = pool.Rent(table.All());
            Assert.True(node.IsConcurrent);
            var first = pool.Rent(Subset.Dense(1, 2));
            var second = pool.Rent(Subset.Dense(1, 2));
            Assert.Same(first, node.AddChild(0, 7, first, table.Arity, pool));
            Assert.Same(first, node.AddChild(0, 7, second, table.Arity, pool));

            // The loser was released into the pool.
            Assert.Same(second, pool.Rent(Subset.Empty));
        }

        [Fact]
        public void ConcurrentCachesAgreeAcrossThreads()
        {
            var table = Table();
            var pool = new TrieNodePool(true);
            var node = pool.Rent(table.All());
            var indexes = new ColumnIndex[8];
            var children = new TrieNode[8];
            Parallel.For(0, 8, i =>
            {
                var local = new TrieNodePool(true);
                indexes[i] = node.GetCachedIndex(new ColumnId(1), table);
                if (node.TryGetChild(1, 1, out var child) == false)
                    child = node.AddChild(1, 1, local.Rent(Subset.Dense(1, 2)), table.Arity, local);

                children[i] = child;
            });

            Assert.All(indexes, x => Assert.Same(indexes[0], x));
            Assert.All(children, x => Assert.Same(children[0], x));
        }

        [Fact]
        public void ResetAndReleaseHandChildrenBackToThePool()
        {
            var table = Table();
            var pool = new TrieNodePool(false);
            var node = pool.Rent(table.All());
            node.GetCachedIndex(new ColumnId(1), table);
            var child = node.AddChild(1, 0, pool.Rent(Subset.Dense(0, 1)), table.Arity, pool);
            node.ResetDense(new OffsetRange(3, 5), pool);
            Assert.Equal(2, node.Size);
            Assert.False(node.TryGetChild(1, 0, out _));
            Assert.Same(child, pool.Rent(Subset.Empty));

            node.Release(pool);
            Assert.Same(node, pool.Rent(Subset.Dense(0, 2)));
            Assert.Equal(2, node.Size);
            Assert.False(node.TryGetChild(1, 0, out _));
        }

        [Fact]
        public void PoolKeepsAtMostItsLimit()
        {
            var pool = new TrieNodePool(false);
            var nodes = Enumerable.Range(0, TrieNodePool.MaxPooled + 10).Select(_ => pool.Rent(Subset.Empty)).ToArray();
            foreach (var node in nodes)
                node.Release(pool);

            var again = Enumerable.Range(0, TrieNodePool.MaxPooled + 10).Select(_ => pool.Rent(Subset.Empty)).ToArray();
            Assert.Equal(TrieNodePool.MaxPooled, again.Intersect(nodes).Count());
        }

    }

}
