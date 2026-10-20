using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the key-index reserve of a <see cref="SortedWritesTable"/> merge: a shard reserves for the rest of its
    /// rows at the rate its first rows added keys, an empty shard's first parallel batch for its live folded rows, a
    /// merge of keys the shards hold reserves nothing, and ordinary growth still takes every key past the reserve.
    /// </summary>
    public class MergeIndexReserveTests
    {

        static readonly ParallelHeuristics Parallel4 = new(4);

        static SortedWritesTable NewTable(ParallelHeuristics parallelism)
        {
            return new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, new MinMerge(new ColumnId(1)), parallelism);
        }

        static void StageAndMerge(SortedWritesTable table, IEnumerable<uint[]> rows)
        {
            // Several buffers, as several rule threads would flush.
            var buffers = Enumerable.Range(0, 3).Select(_ => table.NewBuffer()).ToArray();
            var i = 0;
            foreach (var row in rows)
                buffers[i++ % buffers.Length].StageInsert(row);

            foreach (var buffer in buffers)
                buffer.Flush();

            table.Merge(TableTestUtil.EmptyState());
        }

        static int TotalCapacity(SortedWritesTable table)
        {
            return Capacities(table).Sum();
        }

        static int[] Capacities(SortedWritesTable table)
        {
            return Enumerable.Range(0, table.Index.ShardCount).Select(i => table.Index.GetShard(i).Capacity).ToArray();
        }

        /// <summary>
        /// The capacity of the smallest index that holds <paramref name="entries"/> entries.
        /// </summary>
        static int CapacityFor(int entries)
        {
            return RawHashTable<TableEntry>.BucketMaskToCapacity(RawHashTable<TableEntry>.CapacityToBuckets(entries) - 1);
        }

        static void AssertKeys(SortedWritesTable table, int keys)
        {
            var live = TableTestUtil.LiveRows(table).Select(row => row[0]).OrderBy(key => key).ToArray();
            Assert.Equal(Enumerable.Range(0, keys).Select(key => (uint)key), live);
            for (var i = 0; i < table.Index.ShardCount; i++)
                RawHashTableInvariants.Check(table.Index.GetShard(i), default(TableEntryHasher));
        }

        [Fact]
        public void AnEmptyShardReservesAtTheRateItsFirstRowsAddedKeys()
        {
            // Four thousand rows of one key in three buffers: the first 1,024 rows added one key, so the shard reserves
            // for about three more, not for the 2,976 rows left.
            var table = NewTable(ParallelHeuristics.Serial);
            StageAndMerge(table, Enumerable.Range(0, 4000).Select(i => new uint[] { 7, (uint)i }));
            Assert.Equal(1, table.Count);
            Assert.True(table.Index.GetShard(0).Capacity <= 7, $"The shard holds {table.Index.GetShard(0).Capacity} entries.");
        }

        [Fact]
        public void AnEmptyShardSamplesInsideOneLargeBuffer()
        {
            // One buffer of 5,000 rows, a key per ten rows: the sample ends inside the buffer, and the reserve covers the
            // 500 keys at once instead of the 5,000 rows.
            var table = NewTable(ParallelHeuristics.Serial);
            var buffer = table.NewBuffer();
            for (var i = 0; i < 5000; i++)
                buffer.StageInsert(new uint[] { (uint)(i / 10), (uint)i });

            buffer.Flush();
            table.Merge(TableTestUtil.EmptyState());
            Assert.Equal(500, table.Count);
            var capacity = table.Index.GetShard(0).Capacity;
            Assert.True(capacity >= 500 && capacity < 1000, $"The shard holds {capacity} entries.");
            AssertKeys(table, 500);
        }

        [Fact]
        public void AnEmptyShardWithFewerRowsThanTheSampleOnlyGrows()
        {
            // Below the sample there is no estimate and no reserve, even after a buffer that holds every key: the index
            // holds what growth gave the keys.
            var table = NewTable(ParallelHeuristics.Serial);
            StageAndMerge(table, Enumerable.Range(0, 900).Select(i => new uint[] { (uint)(i / 3 % 3), (uint)i }));
            Assert.Equal(3, table.Count);
            Assert.Equal(3, table.Index.GetShard(0).Capacity);
            AssertKeys(table, 3);
        }

        [Fact]
        public void TheReserveCoversOnlyTheRowsAfterTheSample()
        {
            // 3,000 distinct keys: the sample added 1,024 and the reserve covers the other 1,976, so the index is the
            // smallest that holds 3,000 entries rather than one sized for 1,024 more than that.
            var table = NewTable(ParallelHeuristics.Serial);
            StageAndMerge(table, Enumerable.Range(0, 3000).Select(i => new uint[] { (uint)i, 1 }));
            Assert.Equal(CapacityFor(3000), table.Index.GetShard(0).Capacity);
            AssertKeys(table, 3000);
        }

        [Fact]
        public void EachShardReservesForItsShareRoundedUp()
        {
            // Distinct keys on the serial path into several shards: a share of 1,792 rows and one more rounds up to
            // 1,793, which takes the next index size up, so every shard holds 3,584 entries whether it got a few rows
            // more than the share or a few fewer.
            var table = NewTable(Parallel4);
            var keys = table.Index.ShardCount * 1792 + 1;
            Assert.False(Parallel4.ParallelizeTableOp(keys));
            StageAndMerge(table, Enumerable.Range(0, keys).Select(i => new uint[] { (uint)i, 1 }));
            Assert.All(Capacities(table), capacity => Assert.Equal(CapacityFor(1793), capacity));
            AssertKeys(table, keys);
        }

        [Fact]
        public void AShardWithoutStagedRowsReservesNothing()
        {
            var table = NewTable(Parallel4);
            StageAndMerge(table, Enumerable.Range(0, 400).Select(i => new uint[] { (uint)i, 1 }));
            var before = Capacities(table);

            // Two thousand new keys, all of shard 0.
            var shardData = table.Index.ShardData;
            var rows = Enumerable.Range(1000, int.MaxValue - 1000)
                .Select(i => new uint[] { (uint)i, 1 })
                .Where(row => shardData.ShardOf(TableKeyHash.Hash(row, 1)) == 0)
                .Take(2000)
                .ToArray();
            StageAndMerge(table, rows);

            var after = Capacities(table);
            for (var s = 1; s < after.Length; s++)
                Assert.Equal(before[s], after[s]);
        }

        [Theory]
        [InlineData(1024, 1024, 3000, 3000)]
        [InlineData(1, 1334, 2666, 2)]
        [InlineData(512, 1024, 1, 1)]
        [InlineData(0, 1024, 5000, 0)]
        [InlineData(7, 1024, 0, 0)]
        [InlineData(7, 1024, -20, 0)]
        public void TheEstimateScalesTheRemainingRowsByTheSampledRate(int added, long sampled, long remaining, long expected)
        {
            Assert.Equal(expected, SortedWritesTable.EstimatedNewKeys(added, sampled, remaining));
        }

        [Theory]
        [InlineData(0, 5000L, 5000)]
        [InlineData(10, -3L, 0)]
        [InlineData(0, long.MaxValue, RawHashTable<TableEntry>.MaxCapacity)]
        [InlineData(1000, long.MaxValue, RawHashTable<TableEntry>.MaxCapacity - 1000)]
        [InlineData(RawHashTable<TableEntry>.MaxCapacity, 1L, 0)]
        public void TheReserveStopsAtTheMostEntriesAnIndexHolds(int count, long additional, int expected)
        {
            Assert.Equal(expected, SortedWritesTable.IndexReserveRoom(count, additional));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AMergeOfHeldKeysLeavesEveryShardsIndexAlone(bool parallel)
        {
            // Rows that re-derive the keys the shards hold add none, and the sample shows it: no shard reserves.
            var table = NewTable(parallel ? Parallel4 : ParallelHeuristics.Serial);
            StageAndMerge(table, Enumerable.Range(0, 30_000).Select(i => new uint[] { (uint)i, 10 }));
            var before = Capacities(table);

            StageAndMerge(table, Enumerable.Range(0, 30_000).Select(i => new uint[] { (uint)i, 5 }));
            Assert.Equal(before, Capacities(table));
            AssertKeys(table, 30_000);
        }

        [Fact]
        public void GrowthTakesTheKeysPastTheReserve()
        {
            var table = NewTable(ParallelHeuristics.Serial);
            StageAndMerge(table, Enumerable.Range(0, 10).Select(i => new uint[] { (uint)i, 100 }));

            // A thousand new keys, fewer rows than the sample: no reserve, and growth takes them all.
            StageAndMerge(table, Enumerable.Range(10, 1000).Select(i => new uint[] { (uint)i, 5 }));
            Assert.Equal(1010, table.Count);
            AssertKeys(table, 1010);
        }

        [Fact]
        public void TheParallelInsertReservesForTheFoldedRows()
        {
            // 30,000 rows of 3,000 keys take the parallel path; folded by key, the shards reserve for 3,000 entries, not
            // 30,000.
            var table = NewTable(Parallel4);
            StageAndMerge(table, Enumerable.Range(0, 30_000).Select(i => new uint[] { (uint)(i % 3000), (uint)i }));
            Assert.Equal(3000, table.Count);
            Assert.True(TotalCapacity(table) < 30_000 / 2, $"The shards hold {TotalCapacity(table)} entries.");
            AssertKeys(table, 3000);
        }

        [Fact]
        public void TheParallelInsertReservesForTheLiveFoldedRows()
        {
            // Decreasing values: every repeat of a key changes its minimum and leaves the batch row it replaced stale.
            // The reserve counts the 3,000 live rows, not the 30,000 the batches hold.
            var table = NewTable(Parallel4);
            StageAndMerge(table, Enumerable.Range(0, 30_000).Select(i => new uint[] { (uint)(i % 3000), (uint)(30_000 - i) }));
            Assert.Equal(3000, table.Count);
            Assert.True(TotalCapacity(table) < 30_000 / 2, $"The shards hold {TotalCapacity(table)} entries.");
            AssertKeys(table, 3000);
        }

        [Fact]
        public void TheParallelInsertReservesEveryShardForItsNewKeys()
        {
            var table = NewTable(Parallel4);
            StageAndMerge(table, Enumerable.Range(0, 30_000).Select(i => new uint[] { (uint)i, 1 }));
            Assert.Equal(30_000, table.Count);
            AssertKeys(table, 30_000);

            // A second parallel merge into shards with entries: half old keys, half new.
            StageAndMerge(table, Enumerable.Range(15_000, 30_000).Select(i => new uint[] { (uint)i, 0 }));
            Assert.Equal(45_000, table.Count);
            AssertKeys(table, 45_000);
        }

    }

}
