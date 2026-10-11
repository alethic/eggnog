using System;
using System.Diagnostics;

namespace Eggnog.Relations
{

    /// <summary>
    /// A small, single-shard table that one task of a parallel insert uses to fold together the incoming rows of a
    /// batch that share a key, before merging the survivors into the table: egglog's <c>StagedOutputs</c>
    /// (core-relations/src/table/mod.rs:1462-1545).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows are appended to <see cref="Rows"/> as they arrive; when the merge function replaces a staged row, its
    /// output is appended and the old staged row is tombstoned, so <see cref="Rows"/> holds the batch's fold for every
    /// key plus <see cref="StaleCount"/> tombstones. The whole buffer, tombstones included, is then appended to the
    /// table in one block. The merge output is stored here as in egglog (mod.rs:1520-1527).
    /// </para>
    /// <para>
    /// The key index comes from the engine-wide <c>staged_outputs</c> pool and goes back on <see cref="Seal"/> (once
    /// the batch is complete, so that a shard's next batch reuses it) or <see cref="Dispose"/>; the row storage comes
    /// from the array pool and goes back on <see cref="Dispose"/>. Owned by one task at a time; not thread-safe.
    /// </para>
    /// </remarks>
    internal sealed class StagedOutputs : IDisposable
    {

        readonly int keyCount;
        readonly int arity;
        readonly RowBuffer rows;
        readonly uint[] output;
        RawHashTable<TableEntry>? index;
        int staleCount;

        /// <summary>
        /// Initializes an empty instance with room for <paramref name="capacity"/> rows.
        /// </summary>
        /// <param name="keyCount">The table's number of key columns.</param>
        /// <param name="arity">The table's arity.</param>
        /// <param name="capacity">The number of rows to make room for.</param>
        public StagedOutputs(int keyCount, int arity, int capacity)
        {
            this.keyCount = keyCount;
            this.arity = arity;
            index = SharedPool<RawHashTable<TableEntry>, StagedOutputsHashPolicy>.Rent();
            index.Reserve(capacity, default(TableEntryHasher));
            rows = new RowBuffer(arity, capacity);
            output = new uint[arity];
        }

        /// <summary>
        /// Gets the staged rows, tombstones included.
        /// </summary>
        public RowBuffer Rows => rows;

        /// <summary>
        /// Gets the number of tombstones in <see cref="Rows"/>.
        /// </summary>
        public int StaleCount => staleCount;

        /// <summary>
        /// Gets the number of live staged rows: one per distinct key. egglog's <c>len</c>.
        /// </summary>
        public int LiveCount => rows.Count - staleCount;

        /// <summary>
        /// Folds the row starting at <paramref name="source"/>[<paramref name="offset"/>] into the staged rows: appended
        /// if its key is new, merged into the staged row with that key otherwise. Stale rows are ignored. egglog's
        /// <c>StagedOutputs::insert</c>. Expected O(<c>arity</c>) plus the merge. Not after <see cref="Seal"/>.
        /// </summary>
        /// <param name="state">The task's execution state, for the merge function.</param>
        /// <param name="merge">The table's merge function.</param>
        /// <param name="source">The array holding the incoming row.</param>
        /// <param name="offset">The index of the row's first cell.</param>
        public void Insert(ExecutionState state, MergeFunction merge, uint[] source, int offset)
        {
            if (source[offset] == Value.StaleRep)
                return;

            Debug.Assert(index is not null, "The batch is sealed.");
            var keyIndex = index!;
            var keyHash = TableKeyHash.Hash(source, offset, keyCount);
            var match = new KeyMatch(rows.Data, arity, source, offset, keyCount, keyHash);
            var slot = keyIndex.FindOrFindInsertSlot(keyHash, ref match, default(TableEntryHasher));
            var incoming = new ReadOnlySpan<uint>(source, offset, arity);
            if (slot < 0)
            {
                var added = rows.AddRow(incoming);
                keyIndex.InsertInSlot(keyHash, ~slot, new TableEntry(keyHash, added));
                return;
            }

            ref var entry = ref keyIndex.EntryAt(slot);
            if (merge.Merge(state, rows.GetRow(entry.Row), incoming, output) == false)
                return;

            Debug.Assert(incoming[..keyCount].SequenceEqual(output.AsSpan(0, keyCount)), "A merge function changed the key.");
            var merged = rows.AddRow(output);
            rows.SetStale(entry.Row);
            staleCount++;
            entry.Row = merged;
        }

        /// <summary>
        /// Ends the batch: returns the key index to its pool, keeping the rows. No row may be inserted afterwards.
        /// Calling it again does nothing.
        /// </summary>
        public void Seal()
        {
            if (index is { } released)
            {
                index = null;
                SharedPool<RawHashTable<TableEntry>, StagedOutputsHashPolicy>.Return(released);
            }
        }

        /// <summary>
        /// Returns the key index (unless sealed) to its pool and the row storage to the array pool. Calling it again
        /// does nothing.
        /// </summary>
        public void Dispose()
        {
            Seal();
            rows.Dispose();
            staleCount = 0;
        }

    }

}
