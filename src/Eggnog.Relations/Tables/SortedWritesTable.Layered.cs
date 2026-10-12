using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class SortedWritesTable
    {

        /// <summary>
        /// The child context's layer this table is the own table of, or <c>null</c> for a root or bare table: the one
        /// test <see cref="Merge"/> pays to dispatch (X2).
        /// </summary>
        TableLayer? layer;

        /// <summary>
        /// The gate whose live descendants forbid compaction (§6.8, X27): the root context's for a shared root table, the
        /// owner's for a shared layer, none for a context-private or bare table.
        /// </summary>
        DescendantGate? compactionGate;

        /// <summary>
        /// The row an incoming row is prepared in (canonicalized and stamped) before a layered insert probes it.
        /// </summary>
        uint[]? layerScratch;

        int compactionsBlocked;
        volatile bool isShared;

        /// <summary>
        /// Gets the child context's layer this table belongs to, or <c>null</c>.
        /// </summary>
        internal TableLayer? Layer => layer;

        /// <summary>
        /// Gets or sets the gate whose live descendants forbid compaction (§6.8). Set when the table is registered with a
        /// store or created as a layer.
        /// </summary>
        internal DescendantGate? CompactionGate
        {
            get => compactionGate;
            set => compactionGate = value;
        }

        /// <summary>
        /// Gets the number of compactions skipped because the gate had live descendants.
        /// </summary>
        internal int CompactionsBlocked => compactionsBlocked;

        /// <summary>
        /// Gets whether the table is frozen (<see cref="Freeze"/>): sealed, compacted where allowed, its rebuild index
        /// current, and shared read-only by the frozen context's descendants.
        /// </summary>
        internal bool IsShared => isShared;

        /// <summary>
        /// Gets the sort value of the last row appended (the last run's), or 0 when the table is unsorted or empty.
        /// </summary>
        internal uint LastSortValue => runCount > 0 ? runValues[runCount - 1] : 0u;

        /// <summary>
        /// Gets whether writes are staged and not merged: some pending queue is not empty. O(shards).
        /// </summary>
        internal override bool HasPendingWrites
        {
            get
            {
                for (var shard = 0; shard < pending.ShardCount; shard++)
                    if (pending.RowQueue(shard).IsEmpty == false || pending.RemovalQueue(shard).IsEmpty == false)
                        return true;

                return false;
            }
        }

        /// <summary>
        /// Creates the own table of <paramref name="layer"/> (§6.2): an empty table of this table's shape and merge, with
        /// one shard and never parallel, whose merge dispatches to <see cref="MergeLayered"/>, gated for compaction by the
        /// owner's descendants unless the table is context-private.
        /// </summary>
        /// <param name="layer">The layer.</param>
        /// <returns>The own table.</returns>
        internal SortedWritesTable CreateLayer(TableLayer layer)
        {
            ArgumentNullException.ThrowIfNull(layer);
            var table = new SortedWritesTable(keyCount, Arity, sortColumn >= 0 ? new ColumnId(sortColumn) : null, RebuildColumns, merge, ParallelHeuristics.Serial);
            table.layer = layer;
            table.compactionGate = layer.Schema.IsContextPrivate ? null : layer.Owner.Owner.Gate;
            return table;
        }

        /// <summary>
        /// Finds the live row whose key starts at <paramref name="key"/>[<paramref name="offset"/>], given the key's hash:
        /// the body of <see cref="FindRow(uint[], int)"/> without the hash, so a layered lookup hashes once for every
        /// layer (§6.5). Expected O(<see cref="KeyCount"/>).
        /// </summary>
        /// <param name="key">The array holding the key; the caller has checked its bounds.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <param name="keyHash">The key's hash (<see cref="TableKeyHash.Hash(uint[], int, int)"/>).</param>
        /// <returns>The row id, or −1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int FindRowHashed(uint[] key, int offset, ulong keyHash)
        {
            var match = new KeyMatch(data.Data, Arity, key, offset, keyCount, keyHash);
            var shard = hash.ShardFor(keyHash);
            var slot = shard.Find(keyHash, ref match);
            return slot < 0 ? -1 : shard.EntryAt(slot).Row;
        }

        /// <summary>
        /// Merges the staged writes of a child context's layer (§6.6): removals, then inserts, then a gated compaction;
        /// a row added to the store's contradiction table stops the context when its options ask for it (§6.9).
        /// </summary>
        /// <param name="state">The merge state.</param>
        /// <param name="owner">The layer.</param>
        /// <returns>What changed: <see cref="TableChange.Removed"/> also for an ancestor row hidden by a delete.</returns>
        internal TableChange MergeLayered(ExecutionState state, TableLayer owner)
        {
            var removed = DeleteLayered(owner);
            var added = InsertLayered(state, owner);
            MaybeRehash();
            if (added && owner.Schema.IsContradiction)
                owner.Owner.SetContradicted();

            return new TableChange(added, removed);
        }

        /// <summary>
        /// Freezes the table with its context (§8.1, X13): compacts it if its gate allows (<see cref="ForceRehash"/>),
        /// brings its rebuild index up to date when it has rebuild columns (a table without them, such as a nullary
        /// relation or a value-only table, has none: an index needs a key column), marks it shared and seals its pending
        /// state, so staging into it throws. A second call does nothing.
        /// </summary>
        /// <exception cref="InvalidOperationException">Writes are staged and not merged.</exception>
        public override void Freeze()
        {
            if (isShared)
                return;

            if (HasPendingWrites)
                throw new InvalidOperationException("The table has staged writes that are not merged; merge them before freezing.");

            ForceRehash();
            if (toRebuild.Length > 0)
                RefreshRebuildIndex();

            isShared = true;
            pending.Seal();
        }

        /// <summary>
        /// Debug-checks the root's raw-write contract (X38, I21): every row staged into the table and not merged carries
        /// <paramref name="epoch"/> in its sort column. Walks the pending row queues without dequeuing. Called by a root
        /// <see cref="Context.Write{TResult}(Func{ExecutionState, TResult})"/> before it merges; nothing in a release build.
        /// </summary>
        /// <param name="epoch">The write batch's epoch.</param>
        [Conditional("DEBUG")]
        internal void AssertPendingSortCells(uint epoch)
        {
            if (sortColumn < 0)
                return;

            for (var shard = 0; shard < pending.ShardCount; shard++)
            {
                foreach (var buffer in pending.RowQueue(shard))
                {
                    var cells = buffer.Data;
                    var arity = Arity;
                    for (var row = 0; row < buffer.Count; row++)
                    {
                        var offset = row * arity;
                        if (cells[offset] == Value.StaleRep)
                            continue;

                        Debug.Assert(cells[offset + sortColumn] == epoch, $"A raw root write staged a row with timestamp {cells[offset + sortColumn]} in a batch at epoch {epoch} (I21, X38).");
                    }
                }
            }
        }

        /// <summary>
        /// Applies the staged removals of a layer (§6.6), keys as given: an own row is tombstoned; otherwise a row the
        /// context sees through an ancestor is hidden (<see cref="HideReason.Retracted"/>); otherwise nothing. Keys are not
        /// canonicalized: the rows a context sees are canonical in it (I13), so a stale key matches nothing, as at a
        /// rebuilt root. Legality was checked when the removal was staged (§6.7).
        /// </summary>
        /// <returns>Whether a row was tombstoned or hidden.</returns>
        bool DeleteLayered(TableLayer owner)
        {
            pending.TakeTotalRemovals();
            var queue = pending.RemovalQueue(0);
            if (queue.IsEmpty)
                return false;

            var shard = hash.GetShard(0);
            var tables = owner.Owner;
            var top = owner.Depth - 1;
            var removed = false;
            while (queue.TryDequeue(out var keys))
            {
                var source = keys.Buffer?.Data ?? Array.Empty<uint>();
                var count = keys.Count;
                for (var i = 0; i < count; i++)
                {
                    var offset = i * keyCount;
                    var keyHash = TableKeyHash.Hash(source, offset, keyCount);
                    var match = new KeyMatch(data.Data, Arity, source, offset, keyCount, keyHash);
                    if (shard.Remove(keyHash, ref match, out var entry))
                    {
                        if (data.SetStaleShared(entry.Row) == false)
                            staleRows++;

                        removed = true;
                        continue;
                    }

                    if (tables.TryFindVisible(owner.Id, source, offset, keyHash, top, out _, out var j, out var row) && owner.Hide(j, row, HideReason.Retracted))
                    {
                        owner.SetRetracted();
                        removed = true;
                    }
                }

                keys.Dispose();
            }

            return removed;
        }

        /// <summary>
        /// Applies the staged inserts of a layer (§6.6, X38), in queue and staging order. Each incoming row is first
        /// prepared: its id cells are rewritten to their canonical ids in the context and its sort cell to the merge's
        /// epoch. Then an own row with the key merges with it as in a plain table; otherwise the row the context sees
        /// through an ancestor, if any, merges with it: when the merge function returns <c>true</c> its output is appended
        /// and the ancestor row hidden (<see cref="HideReason.KeyHeld"/>), after the output exists; when it returns
        /// <c>false</c> nothing is stored and the ancestor row stays visible; otherwise the row is appended under a new key.
        /// </summary>
        /// <remarks>
        /// Rows of one key fold left from the visible row. A merge function's writes (a <see cref="UnionIdMerge"/>'s union)
        /// stage into the context through its catalog. The preparation costs one find per id cell, in children only
        /// (<see cref="LayerStatistics.RowsCanonicalized"/> counts the rows it changed). If a merge function throws, the
        /// staged inserts not applied are dropped and the exception propagates; the context is then faulted (X12).
        /// </remarks>
        /// <returns>Whether a key was added or a row changed.</returns>
        bool InsertLayered(ExecutionState state, TableLayer owner)
        {
            var total = pending.TakeTotalRows();
            try
            {
                if (total > 0)
                    data.Reserve((int)Math.Min(total, int.MaxValue / Arity));

                return InsertLayeredCore(state, owner);
            }
            catch
            {
                pending.Clear();
                throw;
            }
        }

        /// <summary>
        /// The loop of <see cref="InsertLayered"/>.
        /// </summary>
        bool InsertLayeredCore(ExecutionState state, TableLayer owner)
        {
            var queue = pending.RowQueue(0);
            if (queue.IsEmpty)
                return false;

            var shard = hash.GetShard(0);
            var tables = owner.Owner;
            var find = tables.Owner.UnionFind;
            var arity = Arity;
            var row = layerScratch ??= new uint[arity];
            var output = mergeOutput;
            var timestamp = state.Timestamp;
            var top = owner.Depth - 1;
            var changed = false;
            while (queue.TryDequeue(out var buffer))
            {
                var source = buffer.Data;
                var count = buffer.Count;
                for (var r = 0; r < count; r++)
                {
                    var offset = r * arity;
                    if (source[offset] == Value.StaleRep)
                        continue;

                    Array.Copy(source, offset, row, 0, arity);
                    var canonicalized = false;
                    foreach (var column in toRebuild)
                    {
                        var id = row[column];
                        var canonical = find.Find(id);
                        if (canonical != id)
                        {
                            row[column] = canonical;
                            canonicalized = true;
                        }
                    }

                    if (canonicalized)
                        tables.Statistics.RowsCanonicalized++;

                    if (sortColumn >= 0)
                        row[sortColumn] = timestamp;

                    changed |= InsertPrepared(state, owner, shard, row, output, top);
                }

                buffer.Dispose();
            }

            return changed;
        }

        /// <summary>
        /// Inserts one prepared row of a layer; see <see cref="InsertLayered"/>.
        /// </summary>
        bool InsertPrepared(ExecutionState state, TableLayer owner, RawHashTable<TableEntry> shard, uint[] row, uint[] output, int top)
        {
            var keyHash = TableKeyHash.Hash(row, 0, keyCount);
            var match = new KeyMatch(data.Data, Arity, row, 0, keyCount, keyHash);
            var slot = shard.Find(keyHash, ref match);
            if (slot >= 0)
            {
                ref var entry = ref shard.EntryAt(slot);
                var current = data.GetRow(entry.Row);
                Debug.Assert(current[0] != Value.StaleRep, "The index points at a stale row.");
                if (merge.Merge(state, current, row, output) == false)
                    return false;

                AssertMergeOutput(row, output);
                if (sortColumn >= 0)
                    CheckSortValue(row[sortColumn]);

                var replaced = data.AddRow(output);
                if (sortColumn >= 0)
                    RecordSortValue(row[sortColumn], replaced);

                SetStale(entry.Row);
                entry.Row = replaced;
                return true;
            }

            if (owner.Owner.TryFindVisible(owner.Id, row, 0, keyHash, top, out var ancestor, out var j, out var visible))
            {
                if (merge.Merge(state, ancestor.Rows.GetRow(visible), row, output) == false)
                    return false;

                AssertMergeOutput(row, output);
                if (sortColumn >= 0)
                    CheckSortValue(row[sortColumn]);

                var merged = data.AddRow(output);
                if (sortColumn >= 0)
                    RecordSortValue(row[sortColumn], merged);

                shard.InsertUnique(keyHash, new TableEntry(keyHash, merged), default(TableEntryHasher));

                // Hide after the output exists, so the key is never invisible (I14).
                owner.Hide(j, visible, HideReason.KeyHeld);
                return true;
            }

            if (sortColumn >= 0)
                CheckSortValue(row[sortColumn]);

            var added = data.AddRow(row);
            if (sortColumn >= 0)
                RecordSortValue(row[sortColumn], added);

            shard.InsertUnique(keyHash, new TableEntry(keyHash, added), default(TableEntryHasher));
            return true;
        }

    }

}
