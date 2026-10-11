using System;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    internal sealed partial class ColumnIndex
    {

        /// <summary>
        /// The number of subset positions one task of a parallel build scans: egglog's <c>BATCH_SIZE</c>
        /// (core-relations/src/hash_index/mod.rs:266).
        /// </summary>
        internal const int ParallelBatchSize = 1024;

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// egglog's <c>ColumnIndex::merge_parallel</c> (core-relations/src/hash_index/mod.rs:265-332), in two phases. First,
        /// one task per batch of <see cref="ParallelBatchSize"/> subset positions scans its batch into (value, row) pairs
        /// and splits them by shard with a stable counting sort, into its own region of one shared array. Then one task
        /// per shard walks the batches in order and appends its pairs. Rows reach each shard in ascending order without
        /// egglog's sort of batches by first row, because batch order is position order and the subset is sorted.
        /// egglog scans on the calling thread and only splits in parallel; here the scans run in parallel too, which the
        /// read-only table allows.
        /// </para>
        /// <para>
        /// egglog runs this on a dedicated thread pool so that a caller holding an index's once-lock never steals a task
        /// that waits on the same lock. <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> only runs its
        /// own loop bodies on the calling thread, and those take no locks, so the hazard does not arise; if the pool is
        /// busy the caller does the work itself.
        /// </para>
        /// <para>
        /// Works for full builds (after <see cref="Clear"/>) and for appends: every new row is greater than every indexed
        /// one. Cost: O(pairs) work, a pooled array of one word per pair plus per-batch shard bounds.
        /// </para>
        /// </remarks>
        public void MergeParallel(Table table, ReadOnlySpan<ColumnId> columns, SubsetRef subset)
        {
            var projection = CheckColumns(table, columns);
            var size = subset.Size;
            if (size == 0)
                return;

            var shardCount = shards.Length;
            var batches = (size + ParallelBatchSize - 1) / ParallelBatchSize;
            var stride = ParallelBatchSize * projection.Length;
            var total = (long)batches * stride;
            if (total > Array.MaxLength)
                throw new InvalidOperationException($"Indexing {size} rows by {projection.Length} columns needs more than {Array.MaxLength} pairs.");

            var pairs = PooledArray.Allocate<ulong>((int)total);
            var bounds = PooledArray.Allocate<int>(batches * (shardCount + 1));

            Parallel.For(
                0,
                batches,
                parallelism.Options,
                () => (Pairs: PooledArray.Allocate<ulong>(stride), Shards: PooledArray.Allocate<int>(stride), Cursor: new int[shardCount]),
                (batch, _, local) =>
                {
                    SplitBatch(table, projection, subset, batch, local.Pairs, local.Shards, local.Cursor, pairs, bounds, stride);
                    return local;
                },
                local =>
                {
                    PooledArray.Release(local.Pairs);
                    PooledArray.Release(local.Shards);
                });

            Parallel.For(0, shardCount, parallelism.Options, s => FillShard(s, batches, stride, pairs, bounds));

            PooledArray.Release(pairs);
            PooledArray.Release(bounds);
        }

        /// <summary>
        /// Phase one of <see cref="MergeParallel"/> for one batch: scan it into pairs and move them, grouped by shard and
        /// in row order within each shard, to the batch's region of <paramref name="pairs"/>, recording each shard's
        /// bounds.
        /// </summary>
        void SplitBatch(Table table, int[] projection, SubsetRef subset, int batch, ulong[] scratch, int[] shardOf, int[] cursor, ulong[] pairs, int[] bounds, int stride)
        {
            var sink = new ColumnPairSink(scratch, projection);
            table.ScanGenericBounded(subset, batch * ParallelBatchSize, ParallelBatchSize, ReadOnlySpan<Constraint>.Empty, ref sink);
            var count = sink.Count;

            var shardCount = shards.Length;
            var offsets = bounds.AsSpan(batch * (shardCount + 1), shardCount + 1);
            offsets.Clear();
            for (var i = 0; i < count; i++)
            {
                var shard = shardData.ShardOf(FxHasher.Hash((uint)(scratch[i] >> 32)));
                shardOf[i] = shard;
                offsets[shard + 1]++;
            }

            for (var s = 0; s < shardCount; s++)
            {
                cursor[s] = offsets[s];
                offsets[s + 1] += offsets[s];
            }

            var region = pairs.AsSpan(batch * stride, stride);
            for (var i = 0; i < count; i++)
                region[cursor[shardOf[i]]++] = scratch[i];
        }

        /// <summary>
        /// Phase two of <see cref="MergeParallel"/> for one shard: append the shard's pairs of every batch, in batch
        /// order. Works on a copy of the shard, written back at the end, so that tasks filling adjacent shards do not
        /// write to the same cache lines.
        /// </summary>
        void FillShard(int s, int batches, int stride, ulong[] pairs, int[] bounds)
        {
            var shardCount = shards.Length;
            var shard = shards[s];
            for (var batch = 0; batch < batches; batch++)
            {
                var at = batch * (shardCount + 1) + s;
                var region = batch * stride;
                for (int i = bounds[at], end = bounds[at + 1]; i < end; i++)
                {
                    var pair = pairs[region + i];
                    var value = (uint)(pair >> 32);
                    shard.AddRow(value, FxHasher.Hash(value), (int)(uint)pair);
                }
            }

            shards[s] = shard;
        }

    }

}
