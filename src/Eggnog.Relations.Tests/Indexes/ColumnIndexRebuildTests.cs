using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="ColumnIndex.RebuildForSubset(Table, SubsetRef, ColumnId)"/>: refilling an existing per-subset index
    /// in place, which the join's recycled trie nodes do instead of building a new index per node, gives the same
    /// contents as a fresh build and allocates nothing once the pools are warm.
    /// </summary>
    [Collection(IndexStressCollection.Name)]
    public class ColumnIndexRebuildTests
    {

        static uint[] R(params uint[] cells) => cells;

        /// <summary>
        /// A table of 2000 rows <c>(i, i % 7, i % 3)</c>.
        /// </summary>
        static SortedWritesTable BuildTable()
        {
            var rows = Enumerable.Range(0, 2000).Select(i => R((uint)i, (uint)i % 7, (uint)i % 3)).ToList();
            var table = TableTestUtil.FillTable(rows.Take(1), 1, null, ClosureMerge.TakeNew);
            TableTestUtil.Stage(table, rows.Skip(1).ToArray());
            table.Merge(TableTestUtil.EmptyState());
            return table;
        }

        /// <summary>
        /// Picks every row of <c>[0, 2 * size)</c> whose id is not 1 modulo 3, a sparse subset of about
        /// <c>4 * size / 3</c> rows.
        /// </summary>
        static int[] Picked(int size) => Enumerable.Range(0, 2 * size).Where(r => r % 3 != 1).ToArray();

        [Theory]
        [InlineData(20)]
        [InlineData(700)]
        public void RebuildingADisposedIndexMatchesAFreshBuild(int size)
        {
            var table = BuildTable();
            var picked = Picked(size);
            var sparse = SubsetRef.Sparse(picked, 0, picked.Length);
            foreach (var column in new[] { 1, 2 })
            {
                var index = table.GroupByColumn(SubsetRef.Dense(new OffsetRange(0, size)), new ColumnId(column));
                index.Dispose();
                index.RebuildForSubset(table, sparse, new ColumnId(column));
                var fresh = table.GroupByColumn(sparse, new ColumnId(column));
                IndexTestUtil.AssertExactly(IndexTestUtil.Entries(fresh), IndexTestUtil.Entries(index), $"column {column}");
                fresh.Dispose();
                index.Dispose();
            }
        }

        [Fact]
        public void RebuildingWithoutDisposingReplacesTheContents()
        {
            var table = BuildTable();
            var index = table.GroupByColumn(SubsetRef.Dense(new OffsetRange(0, 100)), new ColumnId(1));
            index.RebuildForSubset(table, SubsetRef.Dense(new OffsetRange(500, 510)), new ColumnId(2));
            var expected = Enumerable.Range(500, 10).GroupBy(r => (uint)r % 3).ToDictionary(g => g.Key, g => g.ToArray());
            IndexTestUtil.AssertExactly(expected, IndexTestUtil.Entries(index), "rebuilt over [500, 510) by column 2");
        }

        /// <summary>
        /// A small sparse subset whose runs move between arena blocks (so the free lists are used) and a subset large
        /// enough for the sort-based build: rebuilding a disposed index in place reuses its object, its shard array, its
        /// arena (short arrays stay with it, long ones go through the pool) and its free-list heads.
        /// </summary>
        [Theory]
        [InlineData(20)]
        [InlineData(700)]
        public void RebuildingInPlaceAllocatesNothing(int size)
        {
            var table = BuildTable();
            var picked = Picked(size);
            var sparse = SubsetRef.Sparse(picked, 0, picked.Length);
            var index = table.GroupByColumn(sparse, new ColumnId(2));
            var keys = index.Count;
            for (var i = 0; i < 50; i++)
            {
                index.Dispose();
                index.RebuildForSubset(table, sparse, new ColumnId(2));
            }

            var wrong = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                index.Dispose();
                index.RebuildForSubset(table, sparse, new ColumnId(2));
                wrong += index.Count == keys ? 0 : 1;
            }

            var perBuild = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000.0;
            Assert.Equal(0, wrong);
            Assert.True(perBuild < 8, $"{perBuild:0.0} bytes per rebuild of {picked.Length} rows");
        }

        [Fact]
        public void SingleColumnProjectionsAreShared()
        {
            var table = BuildTable();
            var first = ColumnIndex.CheckColumns(table, [new ColumnId(1)]);
            Assert.Same(first, ColumnIndex.CheckColumns(table, [new ColumnId(1)]));
            Assert.Equal([1], first);
            Assert.NotSame(ColumnIndex.CheckColumns(table, [new ColumnId(1), new ColumnId(2)]), ColumnIndex.CheckColumns(table, [new ColumnId(1), new ColumnId(2)]));
            Assert.Throws<ArgumentOutOfRangeException>(() => ColumnIndex.CheckColumns(table, [new ColumnId(3)]));
        }

    }

}
