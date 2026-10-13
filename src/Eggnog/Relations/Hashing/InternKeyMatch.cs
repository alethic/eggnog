using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the id of a key in an <see cref="InternTable{TKey}"/> shard by comparing the key stored under the id
    /// with the key being interned.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    internal readonly struct InternKeyMatch<TKey> : IEntryMatch<uint>
        where TKey : notnull, IEquatable<TKey>
    {

        readonly TKey[] keys;
        readonly TKey key;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="keys">A snapshot of the key store that holds every id already in the shard.</param>
        /// <param name="key">The key being interned.</param>
        public InternKeyMatch(TKey[] keys, TKey key)
        {
            this.keys = keys;
            this.key = key;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in uint entry) => keys[entry].Equals(key);

    }

}
