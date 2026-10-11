using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="TupleIndexEntry"/> of a one-cell key passed by value: the stored hash must match and the
    /// key buffer's row must hold the key. The form one-column tuple indexes and compiled plans probe with.
    /// </summary>
    /// <remarks>
    /// The key buffer is read without a bounds check: an entry only names a key row its shard stored, which debug builds
    /// assert.
    /// </remarks>
    internal readonly struct TupleKey1Match : IEntryMatch<TupleIndexEntry>
    {

        readonly uint[] keys;
        readonly ulong hash;
        readonly uint k0;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="keys">The shard's key buffer storage, one cell per key.</param>
        /// <param name="hash">The key's hash.</param>
        /// <param name="k0">The key.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TupleKey1Match(uint[] keys, ulong hash, uint k0)
        {
            this.keys = keys;
            this.hash = hash;
            this.k0 = k0;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in TupleIndexEntry entry)
        {
            Debug.Assert(entry.KeyRow >= 0 && entry.KeyRow < keys.Length);
            return entry.Hash == hash && Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(keys), entry.KeyRow) == k0;
        }

    }

}
