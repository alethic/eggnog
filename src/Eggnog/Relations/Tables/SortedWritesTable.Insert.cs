using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    internal sealed partial class SortedWritesTable
    {

        /// <summary>
        /// Applies every staged insert: egglog's <c>do_insert</c> (core-relations/src/table/mod.rs:678-697). Reserves
        /// room for every staged row, then runs the serial insert, or the parallel one above
        /// <see cref="ParallelHeuristics.TableOpThreshold"/> rows. Both also reserve each shard's index for the keys the
        /// merge may add (<see cref="ReserveIndex"/>), which egglog does not.
        /// </summary>
        /// <remarks>
        /// If a sort check fails or a merge function throws, both inserts leave the table consistent, and this drops
        /// every staged insert they did not apply before rethrowing, so that no half of a failed batch lingers into the
        /// next merge. egglog panics instead.
        /// </remarks>
        /// <param name="state">The state merge functions run in.</param>
        /// <returns>Whether a key was added or a merge changed a row.</returns>
        bool DoInsert(ExecutionState state)
        {
            var total = pending.TakeTotalRows();
            try
            {
                if (total > 0)
                    data.Reserve((int)Math.Min(total, int.MaxValue / Arity));

                return parallelism.ParallelizeTableOp(total) ? ParallelInsert(state, total) : SerialInsert(state, total);
            }
            catch
            {
                pending.Clear();
                throw;
            }
        }

        /// <summary>
        /// The staged rows a shard inserts before a merge reserves its index for the rest (<see cref="SerialInsert"/>,
        /// <see cref="WriteShard"/>): enough rows for their share of new keys to stand for the rest, few enough that the
        /// index grows through them cheaply.
        /// </summary>
        internal const int IndexSampleRows = 1024;

        /// <summary>
        /// Inserts the staged rows one at a time, shard by shard, in queue order and then staging order: egglog's
        /// <c>serial_insert</c> (core-relations/src/table/mod.rs:699-815). For each key the result is the left fold of
        /// the merge function over the table's row and the incoming rows.
        /// </summary>
        /// <remarks>
        /// Each shard reserves its index once for the keys the rest of the merge may add, which egglog does not. The
        /// staged rows cannot be counted by distinct key before the merge, and most may match keys the shard already
        /// holds, so the shard first inserts <see cref="IndexSampleRows"/> rows with ordinary growth, then reserves for
        /// the rest of its share of the staged rows at the rate those rows added keys (<see cref="EstimatedNewKeys"/>).
        /// The share is an even split of the total, since rows are spread by key hash. Rows that match existing keys or
        /// collide on a few new ones reserve almost nothing; a share of new distinct keys reserves its final index at
        /// once. A shard with fewer staged rows than the sample only grows.
        /// </remarks>
        /// <param name="state">The state merge functions run in.</param>
        /// <param name="total">The number of staged rows.</param>
        /// <returns>Whether a key was added or a merge changed a row.</returns>
        bool SerialInsert(ExecutionState state, long total)
        {
            var changed = false;
            var share = (total + hash.ShardCount - 1) / hash.ShardCount;
            for (var shardIndex = 0; shardIndex < hash.ShardCount; shardIndex++)
            {
                var queue = pending.RowQueue(shardIndex);
                var shard = hash.GetShard(shardIndex);
                var keysBefore = shard.Count;

                // The rows inserted while the shard samples, or -1 once it has reserved.
                var sampled = 0;
                while (queue.TryDequeue(out var buffer))
                {
                    var from = 0;
                    if (sampled >= 0)
                    {
                        from = Math.Min(buffer.Count, IndexSampleRows - sampled);
                        changed |= InsertSerial(state, shard, shardIndex, buffer, 0, from);
                        sampled += from;
                        if (sampled == IndexSampleRows)
                        {
                            ReserveIndex(shard, EstimatedNewKeys(shard.Count - keysBefore, sampled, share - sampled));
                            sampled = -1;
                        }
                    }

                    changed |= InsertSerial(state, shard, shardIndex, buffer, from, buffer.Count);
                    buffer.Dispose();
                }
            }

            return changed;
        }

        /// <summary>
        /// Gets the keys <paramref name="remaining"/> more rows are expected to add to a shard whose last
        /// <paramref name="sampled"/> rows added <paramref name="added"/> keys: the remaining rows at the same rate,
        /// rounded up.
        /// </summary>
        /// <param name="added">The keys the sampled rows added.</param>
        /// <param name="sampled">The rows inserted so far; above zero.</param>
        /// <param name="remaining">The rows still to insert; nothing is reserved when it is not above zero.</param>
        /// <returns>The entries to reserve.</returns>
        internal static long EstimatedNewKeys(int added, long sampled, long remaining)
        {
            Debug.Assert(sampled > 0, "The estimate needs sampled rows.");
            return remaining <= 0 ? 0 : (remaining * added + sampled - 1) / sampled;
        }

        /// <summary>
        /// Gets the entries an index of <paramref name="count"/> entries can still reserve of
        /// <paramref name="additional"/>: at most the room left below the most entries an index holds
        /// (<see cref="RawHashTable{TEntry}.MaxCapacity"/>), and never below zero.
        /// </summary>
        /// <param name="count">The index's entries.</param>
        /// <param name="additional">The keys to make room for.</param>
        /// <returns>The entries to reserve.</returns>
        internal static int IndexReserveRoom(int count, long additional)
        {
            return (int)Math.Max(0, Math.Min(additional, (long)RawHashTable<TableEntry>.MaxCapacity - count));
        }

        /// <summary>
        /// Reserves <paramref name="shard"/>'s index for <paramref name="additional"/> more keys, within
        /// <see cref="IndexReserveRoom"/>, so that it grows at most once instead of doubling its way up through
        /// intermediate tables. egglog reserves the rows only (core-relations/src/table/mod.rs:678-680); this reserve only
        /// removes allocation.
        /// </summary>
        /// <param name="shard">The shard's index.</param>
        /// <param name="additional">The keys to make room for.</param>
        static void ReserveIndex(RawHashTable<TableEntry> shard, long additional)
        {
            var room = IndexReserveRoom(shard.Count, additional);
            if (room > 0)
                shard.Reserve(room, default(TableEntryHasher));
        }

        /// <summary>
        /// Inserts the live rows <paramref name="from"/> to <paramref name="to"/> (exclusive) of one staged buffer, all
        /// of which belong to <paramref name="shardIndex"/>.
        /// </summary>
        bool InsertSerial(ExecutionState state, RawHashTable<TableEntry> shard, int shardIndex, RowBuffer incoming, int from, int to)
        {
            Debug.Assert(0 <= from && from <= to && to <= incoming.Count, "The rows lie outside the buffer.");

            var arity = Arity;
            var source = incoming.Data;
            var output = mergeOutput;
            var changed = false;
            for (var r = from; r < to; r++)
            {
                var offset = r * arity;
                if (source[offset] == Value.StaleRep)
                    continue;

                var keyHash = TableKeyHash.Hash(source, offset, keyCount);
                Debug.Assert(hash.ShardData.ShardOf(keyHash) == shardIndex, "A row was staged into the wrong shard.");

                var row = new ReadOnlySpan<uint>(source, offset, arity);
                var match = new KeyMatch(data.Data, arity, source, offset, keyCount, keyHash);
                var slot = shard.Find(keyHash, ref match);
                if (slot >= 0)
                {
                    // The key exists: the merge function decides. Nothing below touches the index before the entry
                    // is updated, so the reference stays valid.
                    ref var entry = ref shard.EntryAt(slot);
                    var current = data.GetRow(entry.Row);
                    Debug.Assert(current[0] != Value.StaleRep, "The index points at a stale row.");
                    if (merge.Merge(state, current, row, output) == false)
                        continue;

                    AssertMergeOutput(row, output);
                    if (sortColumn >= 0)
                        CheckSortValue(row[sortColumn]);

                    var added = data.AddRow(output);
                    if (sortColumn >= 0)
                        RecordSortValue(row[sortColumn], added);

                    SetStale(entry.Row);
                    entry.Row = added;
                    changed = true;
                }
                else
                {
                    if (sortColumn >= 0)
                        CheckSortValue(row[sortColumn]);

                    var added = data.AddRow(row);
                    if (sortColumn >= 0)
                        RecordSortValue(row[sortColumn], added);

                    shard.InsertUnique(keyHash, new TableEntry(keyHash, added), default(TableEntryHasher));
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Inserts the staged rows with one task per shard: egglog's <c>parallel_insert</c>
        /// (core-relations/src/table/mod.rs:817-974), except that it stores the merge output, reports only real changes
        /// and checks the sort order before any write.
        /// </summary>
        /// <remarks>
        /// <para>
        /// egglog's shard task folds the shard's rows by key into a <see cref="StagedOutputs"/> (phase 1), appends the
        /// staged rows to the table with one reservation per batch (phase 2), then merges each staged row into the
        /// table's row with the same key (phase 3), flushing every <see cref="ParallelInsertBatchSize"/> keys. Here
        /// phase 1 runs for every shard first, each shard keeping its batches, and phases 2 and 3 follow in a second
        /// parallel loop. The folds and merges are the same, batch for batch, and so is the result.
        /// </para>
        /// <para>
        /// The split puts egglog's sort check between the two loops: every live staged row must carry one sort value, at
        /// least the table's last (egglog's <c>SortChecker</c>, which checks each shard in phase 3 and the shards against
        /// each other after the loop, panicking with the table half-written). A violation throws before the table is
        /// touched; checked after the writes, a mismatch between shards would leave rows appended out of order, with no
        /// sort run and an inexact stale count.
        /// </para>
        /// <para>
        /// In phase 3 the task owns the row it just appended, so when the merge function returns <c>true</c> its output
        /// is written over that row and the index points at it; egglog points the index at the incoming row and drops
        /// the output (mod.rs:902-907). The task reports a change only for a new key or a merge that returned
        /// <c>true</c>; egglog reports every staged row (mod.rs:871). The tasks' execution-state clones are flushed
        /// once their shard is written, and if any staged a write the caller's <see cref="ExecutionState.Changed"/> is
        /// set, as the serial path does.
        /// </para>
        /// <para>
        /// If a merge function throws, the table stays consistent. In phase 1 nothing has been written yet. In phase 3
        /// the task tombstones the rows it appended but did not index (<see cref="FlushStaged"/>), and the rows go back
        /// to the table with an exact stale count and sort run before the exception propagates.
        /// </para>
        /// </remarks>
        /// <param name="state">The state merge functions run in; each task clones it.</param>
        /// <param name="total">The number of staged rows.</param>
        /// <returns>Whether a key was added or a merge changed a row.</returns>
        bool ParallelInsert(ExecutionState state, long total)
        {
            var shardCount = hash.ShardCount;
            var capacity = (int)Math.Min(ParallelInsertBatchSize, total / shardCount + total / (4 * shardCount) + 16);
            var shards = new ParallelInsertShard[shardCount];
            for (var i = 0; i < shardCount; i++)
                shards[i] = new ParallelInsertShard(i);

            try
            {
                // Phase 1 for every shard: reads only the pending queues and writes only task-local state.
                Parallel.For(0, shardCount, parallelism.Options, i => StageShard(shards[i], state, capacity));

                var hasSortValue = false;
                var sortValue = 0u;
                if (sortColumn >= 0)
                    CheckParallelSortValues(shards, out hasSortValue, out sortValue);

                return WriteShards(shards, state, hasSortValue, sortValue);
            }
            finally
            {
                for (var i = 0; i < shardCount; i++)
                    shards[i].DisposeBatches();
            }
        }

        /// <summary>
        /// Phase 1 of a parallel insert for one shard: folds the shard's staged rows by key into batches of at most
        /// <see cref="ParallelInsertBatchSize"/> keys, and records the sort value they carry. Touches nothing of the
        /// table but the shard's pending queue.
        /// </summary>
        /// <param name="work">The shard's state.</param>
        /// <param name="state">The caller's execution state, which the task clones.</param>
        /// <param name="capacity">The rows to make room for in each batch.</param>
        void StageShard(ParallelInsertShard work, ExecutionState state, int capacity)
        {
            var queue = pending.RowQueue(work.ShardIndex);
            if (queue.IsEmpty)
                return;

            var arity = Arity;
            var local = state.Clone(ExecutionStateCloneSite.InsertShard);
            work.Local = local;
            var staged = new StagedOutputs(keyCount, arity, capacity);
            work.Batches.Add(staged);
            while (queue.TryDequeue(out var buffer))
            {
                var source = buffer.Data;
                var count = buffer.Count;
                for (var r = 0; r < count; r++)
                {
                    var offset = r * arity;
                    if (source[offset] == Value.StaleRep)
                        continue;

                    staged.Insert(local, merge, source, offset);
                    if (staged.LiveCount >= ParallelInsertBatchSize)
                    {
                        SealBatch(work, staged);
                        staged = new StagedOutputs(keyCount, arity, capacity);
                        work.Batches.Add(staged);
                    }
                }

                buffer.Dispose();
            }

            SealBatch(work, staged);
        }

        /// <summary>
        /// Ends a batch of <see cref="StageShard"/>: releases its key index, and checks that its live rows carry the
        /// shard's one sort value. This is egglog's <c>SortChecker::check_local</c> (core-relations/src/table/mod.rs:
        /// 1401-1415), which egglog runs on the same rows in phase 3; the baseline is checked once, for all shards, by
        /// <see cref="CheckParallelSortValues"/>. A violation is recorded, not thrown, so that every task finishes.
        /// </summary>
        /// <param name="work">The shard's state.</param>
        /// <param name="staged">The batch.</param>
        void SealBatch(ParallelInsertShard work, StagedOutputs staged)
        {
            staged.Seal();
            if (sortColumn < 0 || work.SortViolation)
                return;

            var arity = Arity;
            var rows = staged.Rows;
            var source = rows.Data;
            var end = rows.Count * arity;
            var hasValue = work.HasSortValue;
            var value = work.SortValue;
            for (var offset = 0; offset < end; offset += arity)
            {
                if (source[offset] == Value.StaleRep)
                    continue;

                var cell = source[offset + sortColumn];
                if (hasValue == false)
                {
                    hasValue = true;
                    value = cell;
                }
                else if (cell != value)
                {
                    work.SortViolation = true;
                    work.ViolatingValue = cell;
                    break;
                }
            }

            work.HasSortValue = hasValue;
            work.SortValue = value;
        }

        /// <summary>
        /// egglog's <c>SortChecker::check_global</c> (core-relations/src/table/mod.rs:1417-1446) together with the
        /// baseline half of <c>check_local</c>: the live staged rows of every shard carry one sort value, at least the
        /// table's last. Throws <see cref="InvalidOperationException"/> otherwise, before anything is written.
        /// </summary>
        /// <param name="shards">Every shard's state, after phase 1.</param>
        /// <param name="hasSortValue">Whether any live row was staged.</param>
        /// <param name="sortValue">The sort value of the staged rows, when <paramref name="hasSortValue"/>.</param>
        void CheckParallelSortValues(ParallelInsertShard[] shards, out bool hasSortValue, out uint sortValue)
        {
            hasSortValue = false;
            sortValue = 0;
            foreach (var work in shards)
            {
                if (work.SortViolation)
                    ThrowMixedSortValues(work.SortValue, work.ViolatingValue);

                if (work.HasSortValue == false)
                    continue;

                if (hasSortValue && work.SortValue != sortValue)
                    ThrowMixedSortValues(sortValue, work.SortValue);

                hasSortValue = true;
                sortValue = work.SortValue;
            }

            if (hasSortValue)
                CheckSortValue(sortValue);
        }

        /// <summary>
        /// Phases 2 and 3 of a parallel insert for every shard, then egglog's bookkeeping (core-relations/src/table/
        /// mod.rs:955-973): the rows go back to the table, the tasks' tombstones are counted and the sort run is
        /// recorded. The bookkeeping runs also when a merge function throws, so the table's rows, stale count and runs
        /// stay exact on every path.
        /// </summary>
        /// <param name="shards">Every shard's state, after phase 1.</param>
        /// <param name="state">The caller's execution state.</param>
        /// <param name="hasSortValue">Whether the staged rows carry a sort value.</param>
        /// <param name="sortValue">That sort value, already checked against the table's last.</param>
        /// <returns>Whether a key was added or a merge changed a row.</returns>
        bool WriteShards(ParallelInsertShard[] shards, ExecutionState state, bool hasSortValue, uint sortValue)
        {
            var nextOffset = data.Count;
            var writer = data.ParallelWriter();
            try
            {
                Parallel.For(0, shards.Length, parallelism.Options, i => WriteShard(shards[i], writer));
            }
            finally
            {
                data = writer.Finish();
                for (var i = 0; i < shards.Length; i++)
                    staleRows += shards[i].MarkedStale;

                // egglog's update_offsets: every appended row carries the checked sort value (tombstones among them
                // are harmless), so one run covers them.
                if (hasSortValue && data.Count > nextOffset)
                    RecordSortValue(sortValue, nextOffset);
            }

            var changed = false;
            var stateChanged = false;
            foreach (var work in shards)
            {
                changed |= work.Changed;
                stateChanged |= work.StateChanged;
            }

            if (stateChanged)
                state.Changed = true;

            return changed;
        }

        /// <summary>
        /// Phases 2 and 3 of a parallel insert for one shard: writes the batches in order, releasing each once written,
        /// then flushes the task's execution state.
        /// </summary>
        /// <remarks>
        /// The shard reserves its index for the keys the batches may add, which egglog does not. A batch is folded by
        /// key, so its live rows are its distinct keys, but a key may repeat across batches or match one the shard
        /// holds. Into an empty shard every key of the first batch is new, and the shard reserves for all of them. The
        /// first batch that may repeat keys (the first into a shard with entries, the second into an empty one) merges
        /// <see cref="IndexSampleRows"/> rows with ordinary growth, and the shard then reserves once for the live rows
        /// of that batch and every later one at the rate those rows added keys (<see cref="EstimatedNewKeys"/>). Keys
        /// past the estimate take ordinary growth.
        /// </remarks>
        /// <param name="work">The shard's state.</param>
        /// <param name="writer">The table's row writer.</param>
        void WriteShard(ParallelInsertShard work, ParallelRowBufferWriter writer)
        {
            var batches = work.Batches;
            if (batches.Count == 0)
                return;

            var shard = hash.GetShard(work.ShardIndex);
            var output = new uint[Arity];

            // The live rows of the batches not yet written, and the batch that samples.
            long remaining = 0;
            for (var b = 0; b < batches.Count; b++)
                remaining += batches[b].LiveCount;

            var sampleBatch = 0;
            if (shard.Count == 0)
            {
                ReserveIndex(shard, batches[0].LiveCount);
                sampleBatch = 1;
            }

            for (var b = 0; b < batches.Count; b++)
            {
                var staged = batches[b];
                FlushStaged(shard, work, writer, staged, output, b == sampleBatch ? IndexSampleRows : 0, remaining);
                remaining -= staged.LiveCount;
                staged.Dispose();
            }

            var local = work.Local!;
            local.Flush();
            work.StateChanged = local.Changed;
        }

        /// <summary>
        /// Phases 2 and 3 for one batch: appends its rows to the table in one block, then merges each live one into the
        /// table's row with the same key (<see cref="MergeAppendedRows"/>). If the merge function throws, the rows of the
        /// block that are live but not indexed (the one being merged and those after it) are tombstoned before the
        /// exception propagates, so that the table holds no live row its index does not know. With
        /// <paramref name="sampleRows"/> above zero, merges that many rows first and then reserves the shard's index for
        /// the other live rows of this batch and the later ones at the rate the sampled rows added keys.
        /// </summary>
        /// <param name="shard">The shard's index.</param>
        /// <param name="work">The shard's state.</param>
        /// <param name="writer">The table's row writer.</param>
        /// <param name="staged">The batch.</param>
        /// <param name="output">Scratch space for the merge function's output.</param>
        /// <param name="sampleRows">The rows to merge before the reserve, or zero for no reserve.</param>
        /// <param name="remaining">The live rows of this batch and the later ones.</param>
        void FlushStaged(RawHashTable<TableEntry> shard, ParallelInsertShard work, ParallelRowBufferWriter writer, StagedOutputs staged, uint[] output, int sampleRows, long remaining)
        {
            var rows = staged.Rows;
            if (rows.Count == 0)
                return;

            var start = writer.AppendContents(rows);
            work.MarkedStale += staged.StaleCount;

            // The read handle keeps the table's storage in place while this task reads and writes rows of its shard. No
            // append happens on this thread until it is disposed, which happens on every path, so that the writer's
            // Finish finds no open read section.
            var handle = writer.ReadHandle();
            try
            {
                var from = 0;
                if (sampleRows > 0 && rows.Count > sampleRows)
                {
                    from = sampleRows;
                    var keysBefore = shard.Count;
                    var sampled = MergeAppendedRows(shard, work, handle, rows, start, output, 0, from);
                    if (sampled > 0)
                        ReserveIndex(shard, EstimatedNewKeys(shard.Count - keysBefore, sampled, remaining - sampled));
                }

                MergeAppendedRows(shard, work, handle, rows, start, output, from, rows.Count);
            }
            catch
            {
                StaleUnindexedRows(shard, work, handle, rows, start);
                throw;
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        /// Phase 3 for rows <paramref name="from"/> to <paramref name="to"/> (exclusive) of one appended block: indexes
        /// each live row whose key is new, and merges each other one into the table's row with the same key, storing
        /// the merge output in the appended row. The counters of <paramref name="work"/> are exact after every row.
        /// </summary>
        /// <returns>The live rows merged.</returns>
        int MergeAppendedRows(RawHashTable<TableEntry> shard, ParallelInsertShard work, ParallelRowBufferReadHandle handle, RowBuffer rows, int start, uint[] output, int from, int to)
        {
            Debug.Assert(0 <= from && from <= to && to <= rows.Count, "The rows lie outside the block.");

            var arity = Arity;
            var source = rows.Data;
            var table = handle.Data;
            var local = work.Local!;
            var current = start + from;
            var merged = 0;
            for (var i = from; i < to; i++, current++)
            {
                var offset = i * arity;
                if (source[offset] == Value.StaleRep)
                    continue;

                merged++;

                var row = new ReadOnlySpan<uint>(source, offset, arity);
                var keyHash = TableKeyHash.Hash(source, offset, keyCount);
                Debug.Assert(hash.ShardData.ShardOf(keyHash) == work.ShardIndex, "A row was staged into the wrong shard.");
                Debug.Assert(row.SequenceEqual(new ReadOnlySpan<uint>(table, current * arity, arity)), "The appended row differs from the staged row.");

                var match = new KeyMatch(table, arity, source, offset, keyCount, keyHash);
                var slot = shard.FindOrFindInsertSlot(keyHash, ref match, default(TableEntryHasher));
                if (slot < 0)
                {
                    shard.InsertInSlot(keyHash, ~slot, new TableEntry(keyHash, current));
                    work.Changed = true;
                    continue;
                }

                ref var entry = ref shard.EntryAt(slot);
                var existing = new ReadOnlySpan<uint>(table, entry.Row * arity, arity);
                if (merge.Merge(local, existing, row, output))
                {
                    // Store the merge output, not the incoming row (egglog drops it: mod.rs:902-907).
                    AssertMergeOutput(row, output);
                    output.AsSpan().CopyTo(new Span<uint>(table, current * arity, arity));
                    var wasStale = handle.SetStaleShared(entry.Row);
                    Debug.Assert(wasStale == false);
                    entry.Row = current;
                    work.Changed = true;
                }
                else
                {
                    var wasStale = handle.SetStaleShared(current);
                    Debug.Assert(wasStale == false);
                }

                work.MarkedStale++;
            }

            return merged;
        }

        /// <summary>
        /// Cleans up after a merge function threw in <see cref="MergeAppendedRows"/>: tombstones, and counts, every row
        /// of the block appended at <paramref name="start"/> that is live but is not the row the index holds for its
        /// key. Rows already merged are indexed or tombstoned, so this finds exactly the row being merged and those not
        /// reached. Cold: one key lookup per row of the block.
        /// </summary>
        void StaleUnindexedRows(RawHashTable<TableEntry> shard, ParallelInsertShard work, ParallelRowBufferReadHandle handle, RowBuffer rows, int start)
        {
            var arity = Arity;
            var source = rows.Data;
            var count = rows.Count;
            var table = handle.Data;
            for (var i = 0; i < count; i++)
            {
                var offset = i * arity;
                var row = start + i;
                if (source[offset] == Value.StaleRep || handle.IsStale(row))
                    continue;

                var keyHash = TableKeyHash.Hash(source, offset, keyCount);
                var match = new KeyMatch(table, arity, source, offset, keyCount, keyHash);
                var slot = shard.Find(keyHash, ref match);
                if (slot >= 0 && shard.EntryAt(slot).Row == row)
                    continue;

                handle.SetStaleShared(row);
                work.MarkedStale++;
            }
        }

        /// <summary>
        /// Throws the <see cref="InvalidOperationException"/> for a parallel insert whose live rows carry two sort values.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowMixedSortValues(uint first, uint second)
        {
            throw new InvalidOperationException($"Concurrently inserting rows with different sort keys ({first} and {second}).");
        }

    }

}
