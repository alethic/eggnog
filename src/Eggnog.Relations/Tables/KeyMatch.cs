using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="TableEntry"/> of a key held in an array: the stored hash must equal the key's hash and
    /// the key cells of the entry's row must equal the key. egglog's <c>get_entry</c> test closure
    /// (core-relations/src/table/mod.rs:1248-1275).
    /// </summary>
    /// <remarks>
    /// The rows are read without bounds checks: an entry only ever names a row the index's owner stored in
    /// <c>rows</c>, which debug builds assert. Cost: one compare of the hash, then at most <c>keyCount</c> cell
    /// compares for the (rare) entries whose hash matches.
    /// </remarks>
    internal readonly struct KeyMatch : IEntryMatch<TableEntry>
    {

        readonly uint[] rows;
        readonly int arity;
        readonly uint[] key;
        readonly int keyOffset;
        readonly int keyCount;
        readonly ulong hash;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rows">The row storage the index's entries point into.</param>
        /// <param name="arity">The cells per row of <paramref name="rows"/>.</param>
        /// <param name="key">The array holding the key.</param>
        /// <param name="keyOffset">The index of the key's first cell; the key must lie within the array.</param>
        /// <param name="keyCount">The number of key cells.</param>
        /// <param name="hash">The key's hash.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public KeyMatch(uint[] rows, int arity, uint[] key, int keyOffset, int keyCount, ulong hash)
        {
            Debug.Assert(keyOffset >= 0 && key.Length - keyOffset >= keyCount);
            this.rows = rows;
            this.arity = arity;
            this.key = key;
            this.keyOffset = keyOffset;
            this.keyCount = keyCount;
            this.hash = hash;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in TableEntry entry)
        {
            if (entry.Hash != hash)
                return false;

            Debug.Assert(entry.Row >= 0 && ((long)entry.Row + 1) * arity <= rows.Length);
            ref var row = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(rows), entry.Row * arity);
            ref var k = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(key), keyOffset);
            for (var i = 0; i < keyCount; i++)
                if (Unsafe.Add(ref row, i) != Unsafe.Add(ref k, i))
                    return false;

            return true;
        }

    }

}
