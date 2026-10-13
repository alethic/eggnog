using System;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    internal sealed partial class TupleIndex
    {

        /// <summary>
        /// The number of subset positions one task of a parallel build scans: egglog's <c>BATCH_SIZE</c>
        /// (core-relations/src/hash_index/mod.rs:648).
        /// </summary>
        internal const int ParallelBatchSize = 1024;

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// egglog's <c>TupleIndex::merge_parallel</c> (core-relations/src/hash_index/mod.rs:644-729), structured like
        /// <see cref="ColumnIndex.MergeParallel"/>: one task per batch of <see cref="ParallelBatchSize"/> positions scans
        /// its keys, hashes them once, and splits them by shard with a stable counting sort into its region of shared
        /// arrays; then one task per shard appends its keys batch by batch, so rows arrive in ascending order. The
        /// hash computed in the first phase is reused in the second.
        /// </para>
        /// <para>
        /// Cost: O(rows × arity) work; pooled arrays of one row id, one hash and <c>arity</c> cells per row.
        /// </para>
        /// </remarks>
        public void MergeParallel(Table table, ReadOnlySpan<ColumnId> columns, SubsetRef subset)
        {
            CheckColumns(table, columns);
            var size = subset.Size;
            if (size == 0)
                return;

            var projection = columns.ToArray();
            var shardCount = shards.Length;
            var batches = (size + ParallelBatchSize - 1) / ParallelBatchSize;
            var slots = (long)batches * ParallelBatchSize;
            if (slots * arity > Array.MaxLength)
                throw new InvalidOperationException($"Indexing {size} rows by {arity} columns needs more than {Array.MaxLength} cells.");

            var rows = PooledArray.Allocate<int>((int)slots);
            var hashes = PooledArray.Allocate<ulong>((int)slots);
            var keys = PooledArray.Allocate<uint>((int)(slots * arity));
            var bounds = PooledArray.Allocate<int>(batches * (shardCount + 1));

            Parallel.For(
                0,
                batches,
                parallelism.Options,
                () => (Rows: PooledArray.Allocate<int>(ParallelBatchSize), Keys: PooledArray.Allocate<uint>(ParallelBatchSize * arity), Hashes: PooledArray.Allocate<ulong>(ParallelBatchSize), Shards: PooledArray.Allocate<int>(ParallelBatchSize), Cursor: new int[shardCount]),
                (batch, _, local) =>
                {
                    SplitBatch(table, projection, subset, batch, local.Rows, local.Keys, local.Hashes, local.Shards, local.Cursor, rows, hashes, keys, bounds);
                    return local;
                },
                local =>
                {
                    PooledArray.Release(local.Rows);
                    PooledArray.Release(local.Keys);
                    PooledArray.Release(local.Hashes);
                    PooledArray.Release(local.Shards);
                });

            Parallel.For(0, shardCount, parallelism.Options, s => FillShard(s, batches, rows, hashes, keys, bounds));

            PooledArray.Release(rows);
            PooledArray.Release(hashes);
            PooledArray.Release(keys);
            PooledArray.Release(bounds);
        }

        /// <summary>
        /// Phase one of <see cref="MergeParallel"/> for one batch: scan its keys, hash them, and move them, grouped by
        /// shard and in row order within each shard, to the batch's region of the shared arrays, recording each shard's
        /// bounds.
        /// </summary>
        void SplitBatch(Table table, ColumnId[] projection, SubsetRef subset, int batch, int[] localRows, uint[] localKeys, ulong[] localHashes, int[] shardOf, int[] cursor, int[] rows, ulong[] hashes, uint[] keys, int[] bounds)
        {
            var sink = new TupleBatchSink(localRows, localKeys, arity);
            table.ScanProject(subset, projection, batch * ParallelBatchSize, ParallelBatchSize, ReadOnlySpan<Constraint>.Empty, ref sink);
            var count = sink.Count;

            var shardCount = shards.Length;
            var offsets = bounds.AsSpan(batch * (shardCount + 1), shardCount + 1);
            offsets.Clear();
            for (var i = 0; i < count; i++)
            {
                var hash = HashKey(new ReadOnlySpan<uint>(localKeys, i * arity, arity));
                var shard = shardData.ShardOf(hash);
                localHashes[i] = hash;
                shardOf[i] = shard;
                offsets[shard + 1]++;
            }

            for (var s = 0; s < shardCount; s++)
            {
                cursor[s] = offsets[s];
                offsets[s + 1] += offsets[s];
            }

            var region = batch * ParallelBatchSize;
            for (var i = 0; i < count; i++)
            {
                var at = region + cursor[shardOf[i]]++;
                rows[at] = localRows[i];
                hashes[at] = localHashes[i];
                localKeys.AsSpan(i * arity, arity).CopyTo(keys.AsSpan(at * arity, arity));
            }
        }

        /// <summary>
        /// Phase two of <see cref="MergeParallel"/> for one shard: append the shard's keys of every batch, in batch
        /// order, on a copy of the shard written back at the end.
        /// </summary>
        void FillShard(int s, int batches, int[] rows, ulong[] hashes, uint[] keys, int[] bounds)
        {
            var shardCount = shards.Length;
            var shard = shards[s];
            for (var batch = 0; batch < batches; batch++)
            {
                var at = batch * (shardCount + 1) + s;
                var region = batch * ParallelBatchSize;
                for (int i = region + bounds[at], end = region + bounds[at + 1]; i < end; i++)
                    shard.AddRow(hashes[i], keys, i * arity, rows[i]);
            }

            shards[s] = shard;
        }

    }

}
