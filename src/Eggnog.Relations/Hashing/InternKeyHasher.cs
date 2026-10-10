using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recomputes the hash of an id in an <see cref="InternTable{TKey}"/> shard from the key stored under it, as
    /// egglog's <c>InternTable::intern</c> does when its hashbrown table grows.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    internal readonly struct InternKeyHasher<TKey> : IEntryHasher<uint>
        where TKey : notnull, IEquatable<TKey>
    {

        readonly TKey[] keys;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="keys">A snapshot of the key store that holds every id already in the shard.</param>
        public InternKeyHasher(TKey[] keys)
        {
            this.keys = keys;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Hash(in uint entry) => InternTable<TKey>.HashKey(keys[entry]);

    }

}
