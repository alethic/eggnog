using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog
{

    /// <summary>
    /// Makes <see cref="Map{TKey, TValue}"/>s.
    /// </summary>
    public static class Map
    {

        /// <summary>
        /// Makes the map of the given entries: <c>Map.Of((1L, "one"), (2L, "two"))</c>. Of entries with equal keys, the
        /// last one is kept, as egglog's <c>map-insert</c> keeps the last value inserted.
        /// </summary>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <typeparam name="TValue">The value type.</typeparam>
        /// <param name="entries">The entries.</param>
        /// <returns>The map.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="entries"/>, a key or a value is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">Two keys cannot be compared: the key type, or a type a key holds,
        /// is not comparable.</exception>
        public static Map<TKey, TValue> Of<TKey, TValue>(params (TKey Key, TValue Value)[] entries)
            where TKey : notnull
            where TValue : notnull
        {
            ArgumentNullException.ThrowIfNull(entries);
            return Map<TKey, TValue>.Create(entries.Select(e => new KeyValuePair<TKey, TValue>(e.Key, e.Value)));
        }

        /// <summary>
        /// Makes the map of a sequence of entries; of entries with equal keys, the last one is kept.
        /// </summary>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <typeparam name="TValue">The value type.</typeparam>
        /// <param name="entries">The entries.</param>
        /// <returns>The map.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="entries"/>, a key or a value is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">Two keys cannot be compared: the key type, or a type a key holds,
        /// is not comparable.</exception>
        public static Map<TKey, TValue> From<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> entries)
            where TKey : notnull
            where TValue : notnull
        {
            ArgumentNullException.ThrowIfNull(entries);
            return Map<TKey, TValue>.Create(entries);
        }

    }

}
