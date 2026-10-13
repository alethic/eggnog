using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Returns the hash a <see cref="TupleIndexEntry"/> stores, so a tuple index grows without reading its keys: egglog's
    /// <c>|ent| ent.hash</c> hasher (core-relations/src/hash_index/mod.rs:601).
    /// </summary>
    internal readonly struct TupleIndexEntryHasher : IEntryHasher<TupleIndexEntry>
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Hash(in TupleIndexEntry entry) => entry.Hash;

    }

}
