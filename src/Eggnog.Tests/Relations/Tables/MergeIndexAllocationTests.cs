using System;
using System.Runtime.CompilerServices;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The key-index reserve of a <see cref="SortedWritesTable"/> merge allocates each shard's final index once: a merge
    /// of new keys allocates, beyond what the same merge allocates into shards already sized for its keys, the final
    /// indexes and the small tables the first <see cref="SortedWritesTable.IndexSampleRows"/> rows of a shard grow
    /// through, without the intermediate tables that growth by doubling would allocate on the way up (half the final
    /// index or more).
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class MergeIndexAllocationTests
    {

        /// <summary>
        /// The rows of each staged buffer: several buffers, as a run's batches flush them, none of which ends the
        /// sample of <see cref="SortedWritesTable.IndexSampleRows"/> rows at its last row.
        /// </summary>
        const int BufferRows = 1000;

        [Theory]
        [InlineData(false, 0, 100_000)]
        [InlineData(false, 50_000, 100_000)]
        [InlineData(true, 0, 100_000)]
        [InlineData(true, 40_000, 160_000)]
        public void AMergeOfNewKeysAllocatesEachShardsFinalIndexOnce(bool parallel, int held, int added)
        {
            // The parallel path runs its shard tasks on this thread, so that the count sees them.
            var parallelism = new ParallelHeuristics(parallel ? 4 : 1);
            parallelism.Options.TaskScheduler = new InlineTaskScheduler();
            Assert.Equal(parallel, parallelism.ParallelizeTableOp(added));

            // A first run warms up JIT and type loading and gives each shard's final number of keys.
            var warm = Staged(parallelism, held, added);
            MergeBytes(warm);

            // The same merge into shards reserved for those keys beforehand allocates everything but the index.
            var control = Staged(parallelism, held, added);
            for (var s = 0; s < control.Index.ShardCount; s++)
            {
                var shard = control.Index.GetShard(s);
                shard.Reserve(warm.Index.GetShard(s).Count - shard.Count, default(TableEntryHasher));
            }

            var reserved = MergeBytes(control);
            var table = Staged(parallelism, held, added);
            var bytes = MergeBytes(table);
            Assert.Equal(held + added, table.Count);

            var index = 0L;
            for (var s = 0; s < table.Index.ShardCount; s++)
                index += IndexBytes(table.Index.GetShard(s).Capacity);

            var extra = bytes - reserved;
            Assert.True(extra <= index + index / 4, $"The merge allocated {extra} bytes more than into reserved shards; the final indexes are {index} bytes.");
        }

        /// <summary>
        /// Creates a table of <paramref name="held"/> merged keys with <paramref name="added"/> new keys staged in
        /// buffers of <see cref="BufferRows"/> rows, and with room for the new rows reserved: the row storage comes from
        /// the shared array pool, which may hold an array of that size left by another test, so it stays out of the
        /// count.
        /// </summary>
        static SortedWritesTable Staged(ParallelHeuristics parallelism, int held, int added)
        {
            var table = new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, new MinMerge(new ColumnId(1)), parallelism);
            if (held > 0)
            {
                Stage(table, 0, held);
                table.Merge(TableTestUtil.EmptyState());
            }

            Stage(table, held, held + added);
            table.Rows.Reserve(added);
            return table;
        }

        /// <summary>
        /// Stages the keys <paramref name="from"/> to <paramref name="to"/> (exclusive) in buffers of
        /// <see cref="BufferRows"/> rows.
        /// </summary>
        static void Stage(SortedWritesTable table, int from, int to)
        {
            var row = new uint[2];
            for (var start = from; start < to; start += BufferRows)
            {
                var buffer = table.NewBuffer();
                for (var i = start; i < Math.Min(to, start + BufferRows); i++)
                {
                    row[0] = (uint)i;
                    buffer.StageInsert(row);
                }

                buffer.Flush();
            }
        }

        /// <summary>
        /// Merges the table's staged rows and returns the bytes the merge allocated on this thread.
        /// </summary>
        static long MergeBytes(SortedWritesTable table)
        {
            var state = TableTestUtil.EmptyState();
            var before = GC.GetAllocatedBytesForCurrentThread();
            table.Merge(state);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>
        /// The bytes of an index's entry and control arrays at <paramref name="capacity"/> entries.
        /// </summary>
        static long IndexBytes(int capacity)
        {
            var buckets = (long)RawHashTable<TableEntry>.CapacityToBuckets(Math.Max(capacity, 1));
            return buckets * (Unsafe.SizeOf<TableEntry>() + 1) + ControlGroup.Width;
        }

    }

}
