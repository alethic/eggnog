using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="TupleIndexEntry"/> of a two-cell key passed by value: the stored hash must match and the
    /// key buffer's row must hold the key. The form two-column tuple indexes and compiled plans probe with.
    /// </summary>
    /// <remarks>
    /// The key buffer is read without bounds checks: an entry only names a key row its shard stored, which debug builds
    /// assert.
    /// </remarks>
    internal readonly struct TupleKey2Match : IEntryMatch<TupleIndexEntry>
    {

        readonly uint[] keys;
        readonly ulong hash;
        readonly uint k0;
        readonly uint k1;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="keys">The shard's key buffer storage, two cells per key.</param>
        /// <param name="hash">The key's hash.</param>
        /// <param name="k0">The first cell.</param>
        /// <param name="k1">The second cell.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TupleKey2Match(uint[] keys, ulong hash, uint k0, uint k1)
        {
            this.keys = keys;
            this.hash = hash;
            this.k0 = k0;
            this.k1 = k1;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in TupleIndexEntry entry)
        {
            Debug.Assert(entry.KeyRow >= 0 && ((long)entry.KeyRow + 1) * 2 <= keys.Length);
            if (entry.Hash != hash)
                return false;

            ref var key = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(keys), entry.KeyRow * 2);
            return key == k0 && Unsafe.Add(ref key, 1) == k1;
        }

    }

}
