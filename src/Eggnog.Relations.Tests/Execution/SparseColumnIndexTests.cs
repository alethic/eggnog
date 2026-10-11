using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// egglog's <c>SparseColumnIndex</c> (core-relations/src/free_join/execute.rs:47-153).
    /// </summary>
    public class SparseColumnIndexTests
    {

        static SortedWritesTable Table(params uint[][] rows) => TableTestUtil.FillTable(rows, 1, null, ClosureMerge.TakeNew);

        [Fact]
        public void GroupsRowsByValueInAscendingOrder()
        {
            var table = Table([0, 7], [1, 3], [2, 7], [3, 5], [4, 3]);
            var index = new SparseColumnIndex();
            index.Build(table, SubsetRef.Dense(new OffsetRange(0, 5)), new ColumnId(1));
            Assert.Equal(3, index.Count);
            Assert.Equal([3u, 5u, 7u], Enumerable.Range(0, index.Count).Select(index.KeyAt));
            Assert.Equal([1, 4], ExecutionTestUtil.Ids(index.RowsAt(0)));
            Assert.Equal([3], ExecutionTestUtil.Ids(index.RowsAt(1)));
            Assert.Equal([0, 2], ExecutionTestUtil.Ids(index.RowsAt(2)));

            // A single row comes back dense, so copying it rents nothing.
            Assert.True(index.RowsAt(1).IsDense);
            Assert.True(index.TryGetSubset(7, out var rows));
            Assert.Equal([0, 2], ExecutionTestUtil.Ids(rows));
            Assert.False(index.TryGetSubset(4, out _));
        }

        [Fact]
        public void ListsOnlyLiveRowsOfTheSubset()
        {
            var table = Table([0, 1], [1, 1], [2, 2], [3, 2]);
            TableTestUtil.StageRemove(table, [1]);
            table.Merge(TableTestUtil.EmptyState());
            Assert.True(table.HasStaleRows);

            var index = new SparseColumnIndex();
            var sparse = Subset.Empty;
            sparse.AddRowSorted(1);
            sparse.AddRowSorted(2);
            index.Build(table, sparse.AsRef(), new ColumnId(1));
            Assert.Equal(1, index.Count);
            Assert.Equal(2u, index.KeyAt(0));
            Assert.Equal([2], ExecutionTestUtil.Ids(index.RowsAt(0)));
            sparse.Dispose();
        }

        [Fact]
        public void RebuildsInPlace()
        {
            var table = Table([0, 1], [1, 2]);
            var index = new SparseColumnIndex();
            index.Build(table, SubsetRef.Dense(new OffsetRange(0, 2)), new ColumnId(1));
            index.Build(table, SubsetRef.Dense(new OffsetRange(1, 2)), new ColumnId(0));
            Assert.Equal(1, index.Count);
            Assert.Equal(1u, index.KeyAt(0));
            index.Build(table, SubsetRef.Dense(new OffsetRange(0, 0)), new ColumnId(0));
            Assert.Equal(0, index.Count);
            Assert.False(index.TryGetSubset(1, out _));
        }

        [Fact]
        public void HoldsAtMostEightRows()
        {
            var table = Table(Enumerable.Range(0, 9).Select(i => new[] { (uint)i, 0u }).ToArray());
            Assert.Throws<ArgumentException>(() => new SparseColumnIndex().Build(table, SubsetRef.Dense(new OffsetRange(0, 9)), new ColumnId(1)));
        }

    }

}
