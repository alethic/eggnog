using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="PredictedEntry"/> of a (table, key) pair: same hash, same table, and the same key
    /// cells, compared in the cache's arena.
    /// </summary>
    internal readonly struct PredictedEntryMatch : IEntryMatch<PredictedEntry>
    {

        readonly uint[] cells;
        readonly uint[] key;
        readonly int keyOffset;
        readonly int keyLength;
        readonly int table;
        readonly ulong hash;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cells">The cache's arena.</param>
        /// <param name="table">The table id's index.</param>
        /// <param name="key">The array holding the key.</param>
        /// <param name="keyOffset">The index of the key's first cell.</param>
        /// <param name="keyLength">The number of key cells.</param>
        /// <param name="hash">The hash of the table id and the key.</param>
        public PredictedEntryMatch(uint[] cells, int table, uint[] key, int keyOffset, int keyLength, ulong hash)
        {
            this.cells = cells;
            this.table = table;
            this.key = key;
            this.keyOffset = keyOffset;
            this.keyLength = keyLength;
            this.hash = hash;
        }

        /// <inheritdoc />
        public bool Matches(in PredictedEntry entry)
        {
            return entry.Hash == hash
                && entry.Table == table
                && entry.KeyLength == keyLength
                && new ReadOnlySpan<uint>(cells, entry.Offset, keyLength).SequenceEqual(new ReadOnlySpan<uint>(key, keyOffset, keyLength));
        }

    }

}
