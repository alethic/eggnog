using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="TupleIndexEntry"/> of a three-cell key passed by value: the stored hash must match and
    /// the key buffer's row must hold the key. The form three-column tuple indexes probe with.
    /// </summary>
    /// <remarks>
    /// The key buffer is read without bounds checks: an entry only names a key row its shard stored, which debug builds
    /// assert.
    /// </remarks>
    internal readonly struct TupleKey3Match : IEntryMatch<TupleIndexEntry>
    {

        readonly uint[] keys;
        readonly ulong hash;
        readonly uint k0;
        readonly uint k1;
        readonly uint k2;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="keys">The shard's key buffer storage, three cells per key.</param>
        /// <param name="hash">The key's hash.</param>
        /// <param name="k0">The first cell.</param>
        /// <param name="k1">The second cell.</param>
        /// <param name="k2">The third cell.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TupleKey3Match(uint[] keys, ulong hash, uint k0, uint k1, uint k2)
        {
            this.keys = keys;
            this.hash = hash;
            this.k0 = k0;
            this.k1 = k1;
            this.k2 = k2;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in TupleIndexEntry entry)
        {
            Debug.Assert(entry.KeyRow >= 0 && ((long)entry.KeyRow + 1) * 3 <= keys.Length);
            if (entry.Hash != hash)
                return false;

            ref var key = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(keys), entry.KeyRow * 3);
            return key == k0 && Unsafe.Add(ref key, 1) == k1 && Unsafe.Add(ref key, 2) == k2;
        }

    }

}
