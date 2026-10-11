using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="TupleIndexEntry"/> of a key of any arity held in an array: the stored hash must match
    /// and the key buffer's row must equal the key. egglog's <c>find</c> closure
    /// (core-relations/src/hash_index/mod.rs:583-586).
    /// </summary>
    /// <remarks>
    /// Cost: one hash compare, then a vectorized compare of <c>arity</c> cells for the (rare) entries whose hash matches.
    /// </remarks>
    internal readonly struct TupleKeyMatch : IEntryMatch<TupleIndexEntry>
    {

        readonly uint[] keys;
        readonly int arity;
        readonly ulong hash;
        readonly uint[] key;
        readonly int offset;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="keys">The shard's key buffer storage, <paramref name="arity"/> cells per key.</param>
        /// <param name="arity">The key arity.</param>
        /// <param name="hash">The key's hash.</param>
        /// <param name="key">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell; the key must lie within the array.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TupleKeyMatch(uint[] keys, int arity, ulong hash, uint[] key, int offset)
        {
            Debug.Assert(offset >= 0 && key.Length - offset >= arity);
            this.keys = keys;
            this.arity = arity;
            this.hash = hash;
            this.key = key;
            this.offset = offset;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in TupleIndexEntry entry)
        {
            if (entry.Hash != hash)
                return false;

            return new ReadOnlySpan<uint>(keys, entry.KeyRow * arity, arity).SequenceEqual(new ReadOnlySpan<uint>(key, offset, arity));
        }

    }

}
