using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The rows an execution state has staged for keys missing from their tables, by (table, key): egglog's
    /// <c>PredictedVals</c> (core-relations/src/action/mod.rs:292-323), a map from <c>(TableId, key)</c> to the staged row.
    /// It makes "look up or insert a fresh id" agree with itself within one execution state: the second lane (or batch)
    /// that misses the same key gets the row, and so the id, the first one staged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows live back to back in one cell arena (<see cref="Cells"/>), and a SwissTable of <see cref="PredictedEntry"/>
    /// indexes them by the hash of the table id and the key, comparing keys in the arena: no allocation per entry,
    /// where egglog allocates a small vector for the key and a pooled vector for the row.
    /// </para>
    /// <para>
    /// Lookups that miss reserve the row in two steps. <see cref="FindOrBegin"/> finds the row or copies the key into
    /// fresh arena space and remembers where the entry goes; the caller writes the value cells and stages the row, then
    /// calls <see cref="Commit"/>. A reservation that is never committed (the caller threw) is abandoned by the next
    /// <see cref="FindOrBegin"/>; the cache is unchanged.
    /// </para>
    /// <para>
    /// Not thread-safe: one per execution state, and clones start with an empty cache, as egglog's do.
    /// </para>
    /// </remarks>
    internal sealed class PredictedValues
    {

        readonly RawHashTable<PredictedEntry> index;
        uint[] cells;
        int used;
        uint[] probe;

        int pendingSlot;
        ulong pendingHash;
        int pendingTable;
        int pendingOffset;
        int pendingKeyLength;
        int pendingRowLength;

        /// <summary>
        /// Initializes an empty cache.
        /// </summary>
        public PredictedValues()
        {
            index = new RawHashTable<PredictedEntry>();
            cells = Array.Empty<uint>();
            probe = Array.Empty<uint>();
            pendingSlot = -1;
        }

        /// <summary>
        /// Gets the number of rows predicted.
        /// </summary>
        public int Count => index.Count;

        /// <summary>
        /// Gets the arena holding every predicted row. Offsets returned by <see cref="TryGetRow"/> and
        /// <see cref="FindOrBegin"/> index it. Replaced when it grows, so read it again after
        /// <see cref="FindOrBegin"/>.
        /// </summary>
        public uint[] Cells => cells;

        /// <summary>
        /// Gets a scratch array of at least <paramref name="length"/> cells for a caller holding a key in a span to copy
        /// it into before probing. Reused by every call.
        /// </summary>
        /// <param name="length">The number of cells needed.</param>
        /// <returns>The array.</returns>
        public uint[] GetProbeBuffer(int length)
        {
            if (probe.Length < length)
                probe = new uint[Math.Max(length, 8)];

            return probe;
        }

        /// <summary>
        /// Hashes a table id and a key: FxHash over the table id, then each key cell.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="key">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <param name="length">The number of key cells.</param>
        /// <returns>The 64-bit hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Hash(TableId table, uint[] key, int offset, int length)
        {
            var hasher = new FxHasher();
            hasher.Write((uint)table.Rep);
            for (var i = 0; i < length; i++)
                hasher.Write(key[offset + i]);

            return hasher.Finish();
        }

        /// <summary>
        /// Finds the predicted row for <paramref name="table"/> and the key. Expected O(key length).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="key">The array holding the key.</param>
        /// <param name="keyOffset">The index of the key's first cell.</param>
        /// <param name="keyLength">The number of key cells.</param>
        /// <param name="rowOffset">The index of the row's first cell in <see cref="Cells"/>.</param>
        /// <returns><c>true</c> if a row was predicted.</returns>
        public bool TryGetRow(TableId table, uint[] key, int keyOffset, int keyLength, out int rowOffset)
        {
            CheckKey(key, keyOffset, keyLength);
            var hash = Hash(table, key, keyOffset, keyLength);
            var match = new PredictedEntryMatch(cells, table.Rep, key, keyOffset, keyLength, hash);
            if (index.TryFind(hash, ref match, out var entry))
            {
                rowOffset = entry.Offset;
                return true;
            }

            rowOffset = -1;
            return false;
        }

        /// <summary>
        /// Finds the predicted row for <paramref name="table"/> and the key, or reserves <paramref name="rowLength"/>
        /// cells for a new one with the key copied into its first cells. A reservation must be finished with
        /// <see cref="Commit"/> before any other call that changes the cache. Expected O(row length).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="key">The array holding the key.</param>
        /// <param name="keyOffset">The index of the key's first cell.</param>
        /// <param name="keyLength">The number of key cells.</param>
        /// <param name="rowLength">The number of cells of a new row; at least <paramref name="keyLength"/>.</param>
        /// <param name="found"><c>true</c> if the row exists; <c>false</c> if it was reserved.</param>
        /// <returns>The index of the row's first cell in <see cref="Cells"/>.</returns>
        public int FindOrBegin(TableId table, uint[] key, int keyOffset, int keyLength, int rowLength, out bool found)
        {
            CheckKey(key, keyOffset, keyLength);
            if (rowLength < keyLength)
                throw new ArgumentOutOfRangeException(nameof(rowLength), rowLength, "A row holds at least its key.");

            var hash = Hash(table, key, keyOffset, keyLength);
            var match = new PredictedEntryMatch(cells, table.Rep, key, keyOffset, keyLength, hash);
            var slot = index.FindOrFindInsertSlot(hash, ref match, new PredictedEntryHasher());
            if (slot >= 0)
            {
                found = true;
                return index.EntryAt(slot).Offset;
            }

            if (cells.Length - used < rowLength)
                Grow(rowLength);

            Array.Copy(key, keyOffset, cells, used, keyLength);
            pendingSlot = ~slot;
            pendingHash = hash;
            pendingTable = table.Rep;
            pendingOffset = used;
            pendingKeyLength = keyLength;
            pendingRowLength = rowLength;
            found = false;
            return used;
        }

        /// <summary>
        /// Records the row reserved by the last <see cref="FindOrBegin"/>, whose cells the caller has written.
        /// </summary>
        /// <exception cref="InvalidOperationException">No row is reserved.</exception>
        public void Commit()
        {
            if (pendingSlot < 0)
                ThrowNotPending();

            index.InsertInSlot(pendingHash, pendingSlot, new PredictedEntry(pendingHash, pendingTable, pendingOffset, pendingKeyLength, pendingRowLength));
            used = pendingOffset + pendingRowLength;
            pendingSlot = -1;
        }

        /// <summary>
        /// Forgets every predicted row; keeps the arrays. egglog's <c>Clear for PredictedVals</c>.
        /// </summary>
        public void Clear()
        {
            index.Clear();
            used = 0;
            pendingSlot = -1;
        }

        /// <summary>
        /// Grows the arena to hold at least <paramref name="needed"/> more cells.
        /// </summary>
        void Grow(int needed)
        {
            var capacity = Math.Max(Math.Max(64, cells.Length * 2), used + needed);
            Array.Resize(ref cells, capacity);
        }

        static void CheckKey(uint[] key, int offset, int length)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (offset < 0 || length < 0 || key.Length - offset < length)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "The key lies outside the array.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNotPending()
        {
            throw new InvalidOperationException("No predicted row is reserved.");
        }

    }

}
