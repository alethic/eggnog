using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace Eggnog
{

    /// <summary>
    /// An immutable map of values or classes, equal by content: egglog's <c>Map</c> container (src/sort/map.rs), whose
    /// column type <see cref="ValueStore.RegisterMap{TKey, TValue}"/> registers and whose primitives are
    /// <see cref="Primitives"/>' <c>Map*</c> methods. A map is interned as a container, so equal maps have equal cells. It
    /// enumerates in ascending key order, as <see cref="ValueSet{T}"/> orders its elements (by code point for strings, as
    /// egglog's <c>OrderedFloat</c> for doubles, NaN last, by cell for a sort's classes, <c>Comparer&lt;T&gt;.Default</c>
    /// otherwise), so the key type must be <c>string</c>, comparable or a sort, and its order must agree with its
    /// equality; egglog orders a map by its keys' cells, which is the order its terms, serialized nodes and cells' text
    /// list. Operations return a new map, or this one when nothing changes.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    public sealed class Map<TKey, TValue>
        : IReadOnlyDictionary<TKey, TValue>, IEquatable<Map<TKey, TValue>>, IComparable<Map<TKey, TValue>>, IMap
        where TKey : notnull
        where TValue : notnull
    {

        static IComparer<TKey> Order => ValueSetOrder<TKey>.Comparer;

        readonly TKey[] keys;
        readonly TValue[] values;
        int hash;

        /// <summary>
        /// Initializes a map over entries sorted by key, the keys distinct; not copied.
        /// </summary>
        Map(TKey[] keys, TValue[] values)
        {
            this.keys = keys;
            this.values = values;
        }

        /// <summary>
        /// Gets the empty map, egglog's <c>map-empty</c>.
        /// </summary>
        public static Map<TKey, TValue> Empty { get; } = new(Array.Empty<TKey>(), Array.Empty<TValue>());

        /// <summary>
        /// Gets the number of entries: egglog's <c>map-length</c>.
        /// </summary>
        public int Count => keys.Length;

        /// <summary>
        /// Gets the keys, ascending.
        /// </summary>
        public IEnumerable<TKey> Keys => Array.AsReadOnly(keys);

        /// <summary>
        /// Gets the values, in key order.
        /// </summary>
        public IEnumerable<TValue> Values => Array.AsReadOnly(values);

        /// <inheritdoc />
        IEnumerable<(object Key, object Value)> IMap.Entries
        {
            get
            {
                for (var i = 0; i < keys.Length; i++)
                    yield return (keys[i], values[i]);
            }
        }

        /// <summary>
        /// Gets the value of <paramref name="key"/>: egglog's <c>map-get</c>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The value.</returns>
        /// <exception cref="KeyNotFoundException">The key is not in the map.</exception>
        public TValue this[TKey key] =>
            TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The map has no key {key}.");

        /// <summary>
        /// Gets whether the map has <paramref name="key"/>: egglog's <c>map-contains</c>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>Whether it has the key.</returns>
        public bool ContainsKey(TKey key) => IndexOf(key) >= 0;

        /// <summary>
        /// Gets the value of <paramref name="key"/>, if the map has it.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value, when the result is <c>true</c>.</param>
        /// <returns>Whether the map has the key.</returns>
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            var at = IndexOf(key);
            if (at < 0)
            {
                value = default;
                return false;
            }

            value = values[at];
            return true;
        }

        /// <summary>
        /// Gets the map with <paramref name="key"/> mapped to <paramref name="value"/>, replacing any value it had:
        /// egglog's <c>map-insert</c>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns>The new map.</returns>
        /// <exception cref="ArgumentNullException">The key or the value is <c>null</c>.</exception>
        public Map<TKey, TValue> SetItem(TKey key, TValue value)
        {
            if (key is null)
                throw new ArgumentNullException(nameof(key));

            if (value is null)
                throw new ArgumentNullException(nameof(value));

            var at = IndexOf(key);
            if (at >= 0)
            {
                if (EqualityComparer<TValue>.Default.Equals(values[at], value))
                    return this;

                var replaced = (TValue[])values.Clone();
                replaced[at] = value;
                return new(keys, replaced);
            }

            var insert = ~at;
            return new([.. keys[..insert], key, .. keys[insert..]], [.. values[..insert], value, .. values[insert..]]);
        }

        /// <summary>
        /// Gets the map without <paramref name="key"/>: egglog's <c>map-remove</c>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The new map; this instance when the key is not in it.</returns>
        public Map<TKey, TValue> Remove(TKey key)
        {
            var at = IndexOf(key);
            if (at < 0)
                return this;

            return new([.. keys[..at], .. keys[(at + 1)..]], [.. values[..at], .. values[(at + 1)..]]);
        }

        /// <summary>
        /// Gets whether <paramref name="other"/> has the same entries.
        /// </summary>
        /// <param name="other">The other map.</param>
        /// <returns>Whether the maps are equal.</returns>
        public bool Equals(Map<TKey, TValue>? other)
        {
            if (other is null)
                return false;

            if (ReferenceEquals(this, other))
                return true;

            if (keys.Length != other.keys.Length)
                return false;

            for (var i = 0; i < keys.Length; i++)
            {
                if (EqualityComparer<TKey>.Default.Equals(keys[i], other.keys[i]) == false)
                    return false;

                if (EqualityComparer<TValue>.Default.Equals(values[i], other.values[i]) == false)
                    return false;
            }

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Map<TKey, TValue> other && Equals(other);

        /// <summary>
        /// Compares in content order, element by element as <see cref="ValueSet{T}"/> orders its elements (by code point
        /// for strings, <c>Comparer&lt;T&gt;.Default</c> otherwise), its entries in key order, each by key then value,
        /// a shorter prefix first; <c>null</c> first. Lets containers be the elements of multisets and the keys of maps.
        /// </summary>
        /// <param name="other">The other map.</param>
        /// <returns>Negative, zero or positive.</returns>
        /// <exception cref="ArgumentException">An element type is not comparable.</exception>
        public int CompareTo(Map<TKey, TValue>? other)
        {
            if (other is null)
                return 1;

            var valueOrder = ValueSetOrder<TValue>.Comparer;
            for (var i = 0; i < keys.Length && i < other.keys.Length; i++)
            {
                var c = Order.Compare(keys[i], other.keys[i]);
                if (c == 0)
                    c = valueOrder.Compare(values[i], other.values[i]);

                if (c != 0)
                    return c;
            }

            return keys.Length.CompareTo(other.keys.Length);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            if (hash == 0)
            {
                var combined = new HashCode();
                combined.Add(keys.Length);
                for (var i = 0; i < keys.Length; i++)
                {
                    combined.Add(keys[i]);
                    combined.Add(values[i]);
                }

                var computed = combined.ToHashCode();
                hash = computed == 0 ? 1 : computed;
            }

            return hash;
        }

        /// <summary>
        /// Renders the map as <c>{k: v, …}</c> in key order, numbers in the invariant culture.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString() => "{" + string.Join(", ", keys.Select((k, i) => Text(k) + ": " + Text(values[i]))) + "}";

        /// <inheritdoc />
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            for (var i = 0; i < keys.Length; i++)
                yield return new KeyValuePair<TKey, TValue>(keys[i], values[i]);
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Makes a map of entries in any order; of entries with equal keys, the last one is kept.
        /// </summary>
        /// <param name="entries">The entries.</param>
        /// <returns>The map.</returns>
        /// <exception cref="ArgumentNullException">A key or a value is <c>null</c>.</exception>
        internal static Map<TKey, TValue> Create(IEnumerable<KeyValuePair<TKey, TValue>> entries)
        {
            var list = entries.ToArray();
            if (list.Length == 0)
                return Empty;

            foreach (var (key, value) in list)
            {
                if (key is null)
                    throw new ArgumentNullException(nameof(entries), "A map's key is null.");

                if (value is null)
                    throw new ArgumentNullException(nameof(entries), "A map's value is null.");
            }

            // A stable sort by key, so that of equal keys the last one is kept.
            var order = new int[list.Length];
            for (var i = 0; i < order.Length; i++)
                order[i] = i;

            Array.Sort(order, Comparer<int>.Create((a, b) =>
            {
                var byKey = Order.Compare(list[a].Key, list[b].Key);
                return byKey != 0 ? byKey : a.CompareTo(b);
            }));

            var keys = new List<TKey>(list.Length);
            var values = new List<TValue>(list.Length);
            foreach (var at in order)
            {
                if (keys.Count > 0 && Order.Compare(keys[^1], list[at].Key) == 0)
                {
                    values[^1] = list[at].Value;
                    continue;
                }

                keys.Add(list[at].Key);
                values.Add(list[at].Value);
            }

            return new(keys.ToArray(), values.ToArray());
        }

        /// <summary>
        /// Makes a map of distinct keys, each with its value, in any order: one sort, the arrays owned from now on.
        /// </summary>
        /// <param name="keys">The keys, distinct.</param>
        /// <param name="values">The values, one per key.</param>
        /// <returns>The map.</returns>
        /// <exception cref="ArgumentException">Two keys compare equal.</exception>
        internal static Map<TKey, TValue> FromDistinct(TKey[] keys, TValue[] values)
        {
            if (keys.Length == 0)
                return Empty;

            var sorted = true;
            for (var i = 1; i < keys.Length && sorted; i++)
                sorted = Order.Compare(keys[i - 1], keys[i]) < 0;

            if (sorted == false)
            {
                Array.Sort(keys, values, Order);
                for (var i = 1; i < keys.Length; i++)
                    if (Order.Compare(keys[i - 1], keys[i]) == 0)
                        throw new ArgumentException($"Two of a map's keys compare equal: {keys[i]}.", nameof(keys));
            }

            return new(keys, values);
        }

        /// <summary>
        /// Finds a key by binary search.
        /// </summary>
        /// <returns>Its index, or the complement of where it would be inserted.</returns>
        int IndexOf(TKey key)
        {
            if (key is null)
                throw new ArgumentNullException(nameof(key));

            return Array.BinarySearch(keys, key, Order);
        }

        /// <summary>
        /// Formats a key or value in the invariant culture.
        /// </summary>
        static string Text(object item) => item is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : item.ToString() ?? string.Empty;

    }

}
