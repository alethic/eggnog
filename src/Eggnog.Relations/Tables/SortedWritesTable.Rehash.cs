using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    internal sealed partial class SortedWritesTable
    {

        /// <summary>
        /// Compacts the table when tombstones exceed half its physical rows (and 16): egglog's <c>maybe_rehash</c>
        /// (core-relations/src/table/mod.rs:1036-1046). Sorted tables above
        /// <see cref="ParallelHeuristics.TableOpThreshold"/> rows compact in parallel.
        /// </summary>
        void MaybeRehash()
        {
            if (staleRows <= Math.Max(16, data.Count / 2))
                return;

            if (parallelism.ParallelizeTableOp(data.Count))
                ParallelRehash();
            else
                Rehash();
        }

        /// <summary>
        /// Compacts the table in place, keeping row order: removes every tombstone, renumbers the remaining rows, points
        /// each key's index entry at its row's new id and rebuilds the sort runs. Starts a new generation. egglog's
        /// <c>rehash</c> (core-relations/src/table/mod.rs:1205-1245). O(physical cells) plus one index probe per moved
        /// row.
        /// </summary>
        internal void Rehash()
        {
            generation++;
            runCount = 0;
            var remap = new SortedWritesRemap(this);
            data.RemoveStale(ref remap);
            staleRows = 0;
        }

        /// <summary>
        /// Called by compaction for every live row, in order, after it moved from <paramref name="oldRow"/> to
        /// <paramref name="newRow"/>: repoints the index entry and extends the sort runs.
        /// </summary>
        /// <param name="row">The row's cells.</param>
        /// <param name="oldRow">The row's id before compaction.</param>
        /// <param name="newRow">The row's id after compaction.</param>
        internal void RemapRow(ReadOnlySpan<uint> row, int oldRow, int newRow)
        {
            // Rows before the first tombstone keep their ids, and their entries are already right; egglog looks them
            // up anyway.
            if (oldRow != newRow)
            {
                var keyHash = TableKeyHash.Hash(row, keyCount);
                var match = new RowIdMatch(keyHash, oldRow);
                ref var entry = ref hash.ShardFor(keyHash).FindRef(keyHash, ref match, out var found);
                if (found == false)
                    throw new InvalidOperationException("A live row is not in the key index.");

                entry.Row = newRow;
            }
            else
            {
                Debug.Assert(IndexPointsAt(row, oldRow), "A live row is not in the key index.");
            }

            if (sortColumn >= 0)
                RecordSortValue(row[sortColumn], newRow);
        }

        /// <summary>
        /// Compacts a sorted table with one task per shard: egglog's <c>parallel_rehash</c>
        /// (core-relations/src/table/mod.rs:1047-1204). Unsorted tables compact serially, as in egglog.
        /// </summary>
        /// <remarks>
        /// A histogram of live rows per (sort run, shard) gives, by prefix sums, the destination of every shard's rows
        /// within each run; each task then walks its shard's index entries and copies their rows to disjoint slots of
        /// the scratch buffer, which becomes the row storage. Runs keep their order; rows within a run end up grouped
        /// by shard. Starts a new generation.
        /// </remarks>
        internal void ParallelRehash()
        {
            if (sortColumn < 0)
            {
                Rehash();
                return;
            }

            generation++;
            var runs = runCount;
            var shards = hash.ShardCount;
            Debug.Assert(runs > 0 || data.Count == 0);

            // The runs are rewritten below; the scatter phase still needs the old run values to find a row's run.
            var values = runValues.AsSpan(0, runs).ToArray();
            var starts = runStarts.AsSpan(0, runs).ToArray();
            var counts = new int[runs];
            var histogram = ArrayPool<int>.Shared.Rent(runs * shards);
            Array.Clear(histogram, 0, runs * shards);

            var rows = data;
            var shardData = hash.ShardData;
            Parallel.For(0, runs, parallelism.Options, run =>
            {
                var end = run + 1 < runs ? starts[run + 1] : rows.Count;
                var cells = rows.Data;
                var live = 0;
                for (var row = starts[run]; row < end; row++)
                {
                    var offset = row * Arity;
                    if (cells[offset] == Value.StaleRep)
                        continue;

                    live++;
                    histogram[run * shards + shardData.ShardOf(TableKeyHash.Hash(cells, offset, keyCount))]++;
                }

                counts[run] = live;
            });

            // Serially: the new runs, and each (run, shard) cell turned into the first destination row of that shard's
            // rows in that run.
            var total = 0;
            runCount = 0;
            for (var run = 0; run < runs; run++)
            {
                if (counts[run] == 0)
                    continue;

                runValues[runCount] = values[run];
                runStarts[runCount] = total;
                runCount++;
                var next = total;
                for (var shard = 0; shard < shards; shard++)
                {
                    ref var cell = ref histogram[run * shards + shard];
                    var rowsInShard = cell;
                    cell = next;
                    next += rowsInShard;
                }

                total += counts[run];
                Debug.Assert(next == total);
            }

            scratch.Clear();
            scratch.Reserve(total);
            var target = scratch.Data;
            Parallel.For(0, shards, parallelism.Options, shard =>
            {
                var cells = rows.Data;
                var arity = Arity;
                foreach (ref var entry in hash.GetShard(shard))
                {
                    var offset = entry.Row * arity;
                    Debug.Assert(cells[offset] != Value.StaleRep, "The index points at a stale row.");
                    var run = Array.BinarySearch(values, cells[offset + sortColumn]);
                    Debug.Assert(run >= 0);
                    ref var cell = ref histogram[run * shards + shard];
                    var destination = cell++;
                    new ReadOnlySpan<uint>(cells, offset, arity).CopyTo(new Span<uint>(target, destination * arity, arity));
                    entry.Row = destination;
                }
            });

            ArrayPool<int>.Shared.Return(histogram);
            scratch.SetCount(total);
            (data, scratch) = (scratch, data);
            staleRows = 0;
        }

        /// <summary>
        /// Gets whether the key index entry for <paramref name="row"/>'s key points at <paramref name="rowId"/>.
        /// </summary>
        bool IndexPointsAt(ReadOnlySpan<uint> row, int rowId)
        {
            var keyHash = TableKeyHash.Hash(row, keyCount);
            var match = new RowIdMatch(keyHash, rowId);
            return hash.ShardFor(keyHash).Find(keyHash, ref match) >= 0;
        }

    }

}
