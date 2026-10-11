using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Index storage comes from and goes back to the engine's pools, as egglog's <c>Pooled</c> index maps, hash tables,
    /// key vectors and arenas do (core-relations/src/hash_index/mod.rs:123-144, 191-203, 802-822): sparse subsets, the
    /// sort-based build and repeated keys reuse storage too, shards rent only once they receive a value, and a disposed
    /// index stays usable without sharing storage with the index that rented its table next.
    /// </summary>
    [Collection(IndexStressCollection.Name)]
    public class IndexStoragePoolTests
    {

        const int Builds = 200;

        static uint[] R(params uint[] cells) => cells;

        static ObjectPool<RawHashTable<ColumnIndexEntry>, ColumnIndexHashPolicy> ColumnTables => SharedPool<RawHashTable<ColumnIndexEntry>, ColumnIndexHashPolicy>.Instance;

        static ObjectPool<RawHashTable<TupleIndexEntry>, TupleIndexHashPolicy> TupleTables => SharedPool<RawHashTable<TupleIndexEntry>, TupleIndexHashPolicy>.Instance;

        /// <summary>
        /// A table of 2000 rows <c>(i, i % a, i % b)</c>.
        /// </summary>
        static SortedWritesTable BuildTable(uint a, uint b)
        {
            var rows = Enumerable.Range(0, 2000).Select(i => R((uint)i, (uint)i % a, (uint)i % b)).ToList();
            var table = TableTestUtil.FillTable(rows.Take(1), 1, null, ClosureMerge.TakeNew);
            TableTestUtil.Stage(table, rows.Skip(1).ToArray());
            table.Merge(TableTestUtil.EmptyState());
            return table;
        }

        /// <summary>
        /// Runs <paramref name="build"/> (which builds an index, disposes it and returns its key count) after a warm-up
        /// and returns the bytes it allocated per call on this thread. Every call must return
        /// <paramref name="expectedCount"/>; that is checked after the measurement, so the assertion library's own
        /// allocations are not counted.
        /// </summary>
        static double BytesPerBuild(Func<int> build, int expectedCount)
        {
            var wrong = 0;
            for (var i = 0; i < 50; i++)
                wrong += build() == expectedCount ? 0 : 1;

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Builds; i++)
                wrong += build() == expectedCount ? 0 : 1;

            var perBuild = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Builds;
            Assert.Equal(0, wrong);
            return perBuild;
        }

        /// <summary>
        /// Disposes a freshly built index and returns how many keys it had.
        /// </summary>
        static int Built(ColumnIndex index)
        {
            var count = index.Count;
            index.Dispose();
            return count;
        }

        /// <summary>
        /// Disposes a freshly built index and returns how many keys it had.
        /// </summary>
        static int Built(TupleIndex index)
        {
            var count = index.Count;
            index.Dispose();
            return count;
        }

        [Fact]
        public void SparseGroupByColumnReusesThePooledArena()
        {
            // 40 values, each held by 10 rows 40 apart: every subset is sparse and grows through the arena.
            var table = BuildTable(40, 41);
            var subset = SubsetRef.Dense(new OffsetRange(0, 400));
            var expected = Enumerable.Range(0, 400).GroupBy(i => (uint)(i % 40)).ToDictionary(g => g.Key, g => g.ToArray());

            var check = table.GroupByColumn(subset, new ColumnId(1));
            IndexTestUtil.AssertExactly(expected, IndexTestUtil.Entries(check), "group_by_col");
            check.Dispose();

            var perBuild = BytesPerBuild(() => Built(table.GroupByColumn(subset, new ColumnId(1))), 40);
            Assert.True(perBuild < 1024, $"each sparse group_by_col allocated {perBuild:F0} bytes after warm-up");
        }

        [Fact]
        public void SortBasedGroupByColumnReusesPooledStorage()
        {
            // 2000 rows take the sort-based path; 400 values of 5 rows each.
            var table = BuildTable(400, 401);
            var subset = SubsetRef.Dense(new OffsetRange(0, 2000));
            Assert.True(subset.Size >= ColumnIndex.SortBulkThreshold);
            var expected = Enumerable.Range(0, 2000).GroupBy(i => (uint)(i % 400)).ToDictionary(g => g.Key, g => g.ToArray());

            var check = table.GroupByColumn(subset, new ColumnId(1));
            IndexTestUtil.AssertExactly(expected, IndexTestUtil.Entries(check), "sort-based group_by_col");
            check.Dispose();

            var perBuild = BytesPerBuild(() => Built(table.GroupByColumn(subset, new ColumnId(1))), 400);
            Assert.True(perBuild < 1024, $"each sort-based group_by_col allocated {perBuild:F0} bytes after warm-up");
        }

        [Fact]
        public void GroupByKeyWithRepeatedKeysReusesPooledStorage()
        {
            // Keys (i % 20, i % 10): 20 distinct keys of 20 rows each, all sparse.
            var table = BuildTable(20, 10);
            var subset = SubsetRef.Dense(new OffsetRange(0, 400));
            var columns = IndexTestUtil.Ids(1, 2);
            var expected = Enumerable.Range(0, 400).GroupBy(i => $"{i % 20},{i % 10}").ToDictionary(g => g.Key, g => g.ToArray());

            var check = table.GroupByKey(subset, columns);
            IndexTestUtil.AssertExactly(expected, IndexTestUtil.Entries(check), "group_by_key");
            check.Dispose();

            var perBuild = BytesPerBuild(() => Built(table.GroupByKey(subset, columns)), 20);
            Assert.True(perBuild < 1024, $"each group_by_key with repeated keys allocated {perBuild:F0} bytes after warm-up");
        }

        [Fact]
        public void DisposeReturnsTheTableAndTheNextBuildRentsIt()
        {
            var table = BuildTable(400, 401);
            var subset = SubsetRef.Dense(new OffsetRange(0, 400));
            ColumnTables.Clear();
            TupleTables.Clear();

            var column = table.GroupByColumn(subset, new ColumnId(1));
            Assert.Equal(0, ColumnTables.CountOnCurrentThread);
            column.Dispose();
            Assert.Equal(1, ColumnTables.CountOnCurrentThread);
            column = table.GroupByColumn(subset, new ColumnId(1));
            Assert.Equal(0, ColumnTables.CountOnCurrentThread);
            column.Dispose();

            var tuple = table.GroupByKey(subset, IndexTestUtil.Ids(1, 2));
            Assert.Equal(0, TupleTables.CountOnCurrentThread);
            tuple.Dispose();
            Assert.Equal(1, TupleTables.CountOnCurrentThread);
            tuple = table.GroupByKey(subset, IndexTestUtil.Ids(1, 2));
            Assert.Equal(0, TupleTables.CountOnCurrentThread);
            tuple.Dispose();
        }

        [Fact]
        public void ShardsRentATableOnlyWhenTheyReceiveAValue()
        {
            ColumnTables.Clear();
            TupleTables.Clear();
            var parallelism = new ParallelHeuristics(4);

            // Stock the pools with one table per shard.
            var stock = Enumerable.Range(0, parallelism.ShardData.Count).Select(_ => new ColumnIndex(ParallelHeuristics.Serial)).ToArray();
            var tupleStock = Enumerable.Range(0, parallelism.ShardData.Count).Select(_ => new TupleIndex(2, ParallelHeuristics.Serial)).ToArray();
            foreach (var index in stock)
                index.AddRow(1, 0);
            foreach (var index in tupleStock)
                index.AddRow(R(1, 2), 0);
            foreach (var index in stock)
                index.Dispose();
            foreach (var index in tupleStock)
                index.Dispose();

            var shards = parallelism.ShardData.Count;
            Assert.True(shards > 1);
            Assert.Equal(shards, ColumnTables.CountOnCurrentThread);
            Assert.Equal(shards, TupleTables.CountOnCurrentThread);

            var column = new ColumnIndex(parallelism);
            var tuple = new TupleIndex(2, parallelism);
            Assert.Equal(shards, ColumnTables.CountOnCurrentThread);
            Assert.Equal(shards, TupleTables.CountOnCurrentThread);
            Assert.False(column.TryGetSubset(7, out _));
            Assert.False(tuple.TryGetSubset(7, 8, out _));
            Assert.Equal(0, column.Count);

            column.AddRow(7, 3);
            column.AddRow(7, 5);
            tuple.AddRow(R(7, 8), 3);
            Assert.Equal(shards - 1, ColumnTables.CountOnCurrentThread);
            Assert.Equal(shards - 1, TupleTables.CountOnCurrentThread);

            column.Clear();
            tuple.Clear();
            Assert.Equal(shards - 1, ColumnTables.CountOnCurrentThread);
            Assert.Equal(shards - 1, TupleTables.CountOnCurrentThread);

            column.Dispose();
            tuple.Dispose();
            Assert.Equal(shards, ColumnTables.CountOnCurrentThread);
            Assert.Equal(shards, TupleTables.CountOnCurrentThread);
        }

        [Fact]
        public void ADisposedColumnIndexStaysUsableAndSharesNothing()
        {
            var a = new ColumnIndex(ParallelHeuristics.Serial);
            for (var row = 0; row < 50; row++)
                a.AddRow((uint)(row % 3), row);

            var copy = a.Clone();
            a.Dispose();
            Assert.Equal(0, a.Count);

            // b rents the table and arena a gave back; a then rents others.
            var b = new ColumnIndex(ParallelHeuristics.Serial);
            for (var row = 0; row < 20; row++)
                b.AddRow(9, 2 * row);

            a.AddRow(1, 100);
            a.AddRow(1, 102);
            Assert.Equal(new[] { 100, 102 }, IndexTestUtil.Entries(a)[1]);
            Assert.Single(IndexTestUtil.Entries(a));
            Assert.Equal(Enumerable.Range(0, 20).Select(r => 2 * r), IndexTestUtil.Entries(b)[9]);
            Assert.Single(IndexTestUtil.Entries(b));
            Assert.Equal(Enumerable.Range(0, 50).Where(r => r % 3 == 1), IndexTestUtil.Entries(copy)[1]);
            Assert.Equal(3, copy.Count);

            a.Dispose();
            a.Dispose();
            b.Dispose();
            copy.Dispose();
        }

        [Fact]
        public void ADisposedTupleIndexStaysUsableAndSharesNothing()
        {
            var a = new TupleIndex(3, ParallelHeuristics.Serial);
            for (var row = 0; row < 50; row++)
                a.AddRow(R((uint)(row % 3), 1, 2), row);

            var empty = new TupleIndex(3, new ParallelHeuristics(4));
            var emptyCopy = empty.Clone();
            a.Dispose();
            Assert.Equal(0, a.Count);

            var b = new TupleIndex(3, ParallelHeuristics.Serial);
            for (var row = 0; row < 20; row++)
                b.AddRow(R(9, 9, 9), 2 * row);

            a.AddRow(R(1, 1, 2), 100);
            a.AddRow(R(1, 1, 2), 102);
            Assert.Equal(new[] { 100, 102 }, IndexTestUtil.Entries(a)["1,1,2"]);
            Assert.Single(IndexTestUtil.Entries(a));
            Assert.Equal(Enumerable.Range(0, 20).Select(r => 2 * r), IndexTestUtil.Entries(b)["9,9,9"]);
            Assert.Single(IndexTestUtil.Entries(b));

            emptyCopy.AddRow(R(4, 5, 6), 1);
            Assert.Equal(0, empty.Count);
            Assert.Equal(1, emptyCopy.Count);

            a.Dispose();
            a.Dispose();
            b.Dispose();
            emptyCopy.Dispose();
        }

    }

}
