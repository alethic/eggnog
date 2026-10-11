using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Unit tests for <see cref="ColumnIndex"/> (egglog's core-relations/src/hash_index/mod.rs:205-533): row-at-a-time
    /// building, the multi-column "contains" form, iteration, cloning, and the sort-based and per-subset builds.
    /// </summary>
    public class ColumnIndexTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        static int[] Get(ColumnIndex index, uint key) => index.TryGetSubset(key, out var rows) ? IndexTestUtil.ToArray(rows) : [];

        public static TheoryData<int> Parallelisms() => new() { 1, 4 };

        [Theory]
        [MemberData(nameof(Parallelisms))]
        public void AddRowGroupsRowsByValue(int threads)
        {
            var index = new ColumnIndex(new ParallelHeuristics(threads));
            Assert.Equal(threads == 1 ? 1 : 8, index.ShardCount);
            index.AddRow(10, 0);
            index.AddRow(20, 1);
            index.AddRow(10, 2);
            index.AddRow(10, 3);
            index.AddRow(20, 4);
            index.AddRow(20, 5);

            Assert.Equal(2, index.Count);
            Assert.Equal([0, 2, 3], Get(index, 10));
            Assert.Equal([1, 4, 5], Get(index, 20));
            Assert.False(index.TryGetSubset(30, out _));

            // The gap between rows 0 and 2 moved 10's rows to the arena.
            Assert.True(index.TryGetSubset(10, out var tens));
            Assert.False(tens.IsDense);
        }

        [Fact]
        public void ContiguousRowsStayDense()
        {
            var index = new ColumnIndex(ParallelHeuristics.Serial);
            for (var row = 5; row < 50; row++)
                index.AddRow(7, row);

            Assert.True(index.TryGetSubset(7, out var rows));
            Assert.True(rows.IsDense);
            Assert.Equal(Enumerable.Range(5, 45), IndexTestUtil.ToArray(rows));
        }

        [Fact]
        public void MultiValueRowsAreListedOncePerDistinctValue()
        {
            var index = new ColumnIndex(ParallelHeuristics.Serial);
            index.AddRow(R(1, 2, 1), 0);
            index.AddRow(R(2, 2, 2), 1);
            index.AddRow(R(3, 1, 2), 2);

            Assert.Equal([0, 2], Get(index, 1));
            Assert.Equal([0, 1, 2], Get(index, 2));
            Assert.Equal([2], Get(index, 3));
            Assert.Equal(3, index.Count);
        }

        [Fact]
        public void MergeRowsAddsEveryTaggedRow()
        {
            var buffer = new TaggedRowBuffer(2);
            buffer.AddRow(3, R(5, 6));
            buffer.AddRow(4, R(6, 6));
            buffer.AddRow(9, R(5, 7));
            var index = new ColumnIndex(ParallelHeuristics.Serial);
            index.MergeRows(buffer);

            Assert.Equal([3, 9], Get(index, 5));
            Assert.Equal([3, 4], Get(index, 6));
            Assert.Equal([9], Get(index, 7));
        }

        [Theory]
        [MemberData(nameof(Parallelisms))]
        public void ForEachAndTheCursorVisitEveryEntryInTheSameOrder(int threads)
        {
            var index = new ColumnIndex(new ParallelHeuristics(threads));
            var random = new Random(3);
            var expected = new Dictionary<uint, List<int>>();
            for (var row = 0; row < 500; row++)
            {
                var value = (uint)random.Next(60);
                index.AddRow(value, row);
                if (expected.TryGetValue(value, out var list) == false)
                    expected[value] = list = new List<int>();

                list.Add(row);
            }

            var collector = new ColumnIndexCollector();
            index.ForEach(ref collector);
            Assert.Equal(expected.Count, collector.Order.Count);
            foreach (var (key, rows) in expected)
                Assert.Equal(rows, collector.Entries[key]);

            var cursor = default(ColumnIndexCursor);
            var visited = new List<uint>();
            while (cursor.MoveNext(index))
            {
                visited.Add(cursor.Key);
                Assert.Equal(expected[cursor.Key], IndexTestUtil.ToArray(cursor.Rows));
            }

            Assert.Equal(collector.Order, visited);
            Assert.False(cursor.MoveNext(index));
        }

        [Fact]
        public void ClearKeepsTheIndexUsable()
        {
            var index = new ColumnIndex(ParallelHeuristics.Serial);
            for (var row = 0; row < 100; row++)
                index.AddRow((uint)(row % 7), row);

            index.Clear();
            Assert.Equal(0, index.Count);
            Assert.False(index.TryGetSubset(3, out _));

            index.AddRow(3, 1);
            index.AddRow(3, 4);
            Assert.Equal([1, 4], Get(index, 3));

            index.Dispose();
            Assert.Equal(0, index.Count);
            index.AddRow(9, 2);
            Assert.Equal([2], Get(index, 9));
        }

        [Fact]
        public void CloneIsDeep()
        {
            var index = new ColumnIndex(new ParallelHeuristics(2));
            for (var row = 0; row < 40; row++)
                index.AddRow((uint)(row % 5), row);

            var copy = index.Clone();
            index.AddRow(0, 100);
            index.AddRow(99, 101);

            Assert.Equal([0, 5, 10, 15, 20, 25, 30, 35, 100], Get(index, 0));
            Assert.Equal([0, 5, 10, 15, 20, 25, 30, 35], Get(copy, 0));
            Assert.False(copy.TryGetSubset(99, out _));
            copy.AddRow(0, 200);
            Assert.Equal([0, 5, 10, 15, 20, 25, 30, 35, 100], Get(index, 0));
        }

        [Fact]
        public void ReserveForRowsMakesRoom()
        {
            var index = new ColumnIndex(new ParallelHeuristics(4));
            index.ReserveForRows(1000);
            for (var row = 0; row < 1000; row++)
                index.AddRow((uint)row, row);

            Assert.Equal(1000, index.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => index.ReserveForRows(-1));
        }

        /// <summary>
        /// The multi-column sort-based build: egglog collects pairs column by column, so with at least 64 pairs a value
        /// held in column 1 of an early row and column 0 of a later row gets an unsorted subset, and a row holding a
        /// value in both columns is listed twice unless the two pairs end up adjacent (map §8.1 row 8). The fixed
        /// build lists every value's rows sorted and once.
        /// </summary>
        [Fact]
        public void MultiColumnRebuildFullSortsAndDeduplicates()
        {
            // Value v sits in column 1 of the rows i = v (mod 10) and in column 2 of the rows i = v - 3 (mod 10), so the
            // two columns' rows interleave; every seventh row holds the same value in both columns.
            var rows = new List<uint[]>();
            for (uint i = 0; i < 200; i++)
                rows.Add(R(i, i % 10, i % 7 == 0 ? i % 10 : (i + 3) % 10));

            var table = TableTestUtil.FillTable(rows, 1, null, ClosureMerge.TakeNew);
            var index = new ColumnIndex(ParallelHeuristics.Serial);
            var all = table.All();
            index.RebuildFull(table, IndexTestUtil.Ids(1, 2), all.AsRef());

            var expected = IndexTestUtil.GroupByValue(table, 1, 2);
            Assert.Equal(10, expected.Count);
            IndexTestUtil.AssertExactly(expected, IndexTestUtil.Entries(index), "rebuild_full");

            // The row-at-a-time path agrees.
            var serial = new ColumnIndex(ParallelHeuristics.Serial);
            serial.MergeSubset(table, IndexTestUtil.Ids(1, 2), all.AsRef());
            IndexTestUtil.AssertExactly(expected, IndexTestUtil.Entries(serial), "row at a time");
        }

        [Theory]
        [InlineData(20)]
        [InlineData(511)]
        [InlineData(512)]
        [InlineData(3000)]
        public void BuildForSubsetGroupsTheLiveRowsOfASubset(int rowCount)
        {
            var random = new Random(rowCount);
            var rows = Enumerable.Range(0, rowCount).Select(i => R((uint)i, (uint)random.Next(40), (uint)random.Next(100_000))).ToList();
            var table = TableTestUtil.FillTable(rows, 1, null, ClosureMerge.TakeNew);
            var state = TableTestUtil.EmptyState();
            TableTestUtil.StageRemove(table, Enumerable.Range(0, rowCount).Where(i => i % 9 == 4).Select(i => R((uint)i)).ToArray());
            table.Merge(state);

            foreach (var column in new[] { 1, 2 })
            {
                // The whole table, dense.
                var all = table.All();
                var index = ColumnIndex.BuildForSubset(table, all.AsRef(), C(column));
                Assert.Equal(1, index.ShardCount);
                IndexTestUtil.AssertExactly(IndexTestUtil.GroupByValue(table, column), IndexTestUtil.Entries(index), $"column {column}");

                // A sparse subset.
                var picked = Enumerable.Range(0, table.Rows.Count).Where(r => r % 3 != 1).ToArray();
                var sparse = SubsetRef.Sparse(picked, 0, picked.Length);
                var partial = table.GroupByColumn(sparse, C(column));
                var expected = TableTestUtil.DumpSubset(table, sparse)
                    .GroupBy(x => x.Row[column])
                    .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray());
                IndexTestUtil.AssertExactly(expected, IndexTestUtil.Entries(partial), $"sparse column {column}");
            }
        }

        [Fact]
        public void RejectsBadColumns()
        {
            var table = TableTestUtil.FillTable([R(1, 2)], 1, null, ClosureMerge.TakeNew);
            var index = new ColumnIndex(ParallelHeuristics.Serial);
            var all = table.All();
            Assert.Throws<ArgumentOutOfRangeException>(() => index.MergeSubset(table, [C(2)], all.AsRef()));
            Assert.Throws<ArgumentException>(() => index.MergeSubset(table, ReadOnlySpan<ColumnId>.Empty, all.AsRef()));
            Assert.Throws<ArgumentNullException>(() => new ColumnIndex(null!));
        }

    }

}
