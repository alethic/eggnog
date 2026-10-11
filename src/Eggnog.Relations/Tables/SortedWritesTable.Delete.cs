using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    internal sealed partial class SortedWritesTable
    {

        /// <summary>
        /// Applies every staged removal: egglog's <c>do_delete</c> (core-relations/src/table/mod.rs:668-676). Above
        /// <see cref="ParallelHeuristics.TableOpThreshold"/> removal keys, one task per shard.
        /// </summary>
        /// <returns>Whether any row was removed.</returns>
        bool DoDelete()
        {
            var total = pending.TakeTotalRemovals();
            int marked;
            if (parallelism.ParallelizeTableOp(total))
            {
                var counts = new int[hash.ShardCount];
                Parallel.For(0, counts.Length, parallelism.Options, shard => counts[shard] = DeleteShard(shard));
                marked = 0;
                foreach (var count in counts)
                    marked += count;
            }
            else
            {
                marked = 0;
                for (var shard = 0; shard < hash.ShardCount; shard++)
                    marked += DeleteShard(shard);
            }

            staleRows += marked;
            return marked > 0;
        }

        /// <summary>
        /// Applies the removal keys queued for one shard: for each key, removes its index entry, if any, and tombstones
        /// the row. A missing key is ignored. egglog's <c>serial_delete</c> and <c>parallel_delete</c> loop bodies
        /// (core-relations/src/table/mod.rs:593-666). Touches only the shard's entries and the rows they name, so
        /// distinct shards may run on distinct threads; the stale count is returned rather than updated, and summed by
        /// the caller.
        /// </summary>
        /// <param name="shardIndex">The shard.</param>
        /// <returns>The number of rows tombstoned.</returns>
        int DeleteShard(int shardIndex)
        {
            var queue = pending.RemovalQueue(shardIndex);
            if (queue.IsEmpty)
                return 0;

            var shard = hash.GetShard(shardIndex);
            var rows = data;
            var marked = 0;
            while (queue.TryDequeue(out var keys))
            {
                var source = keys.Buffer?.Data ?? Array.Empty<uint>();
                var count = keys.Count;
                for (var i = 0; i < count; i++)
                {
                    var offset = i * keyCount;
                    var keyHash = TableKeyHash.Hash(source, offset, keyCount);
                    Debug.Assert(hash.ShardData.ShardOf(keyHash) == shardIndex, "A removal was staged into the wrong shard.");

                    var match = new KeyMatch(rows.Data, Arity, source, offset, keyCount, keyHash);
                    if (shard.Remove(keyHash, ref match, out var removed) && rows.SetStaleShared(removed.Row) == false)
                        marked++;
                }

                keys.Dispose();
            }

            return marked;
        }

    }

}
