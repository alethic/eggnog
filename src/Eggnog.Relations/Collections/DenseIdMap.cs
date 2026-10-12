using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A mapping from a numeric identifier to a value, stored as a flat array indexed by the identifier with a
    /// presence flag per slot. Iteration is in ascending identifier order, skipping absent slots.
    /// </summary>
    /// <remarks>
    /// Mirrors egglog's <c>DenseIdMap</c>: <see cref="IdCount"/> counts slots including holes (what bitsets are
    /// sized by), while <see cref="Count"/> counts present entries.
    /// </remarks>
    /// <typeparam name="TKey">The identifier type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    internal sealed class DenseIdMap<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
        where TKey : struct, INumericId<TKey>
    {

        TValue[] values;
        bool[] present;
        int idCount;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public DenseIdMap()
        {
            values = Array.Empty<TValue>();
            present = Array.Empty<bool>();
        }

        /// <summary>
        /// Initializes a new instance with room for <paramref name="capacity"/> identifiers.
        /// </summary>
        /// <param name="capacity">The number of slots to reserve.</param>
        public DenseIdMap(int capacity)
        {
            values = capacity == 0 ? Array.Empty<TValue>() : new TValue[capacity];
            present = capacity == 0 ? Array.Empty<bool>() : new bool[capacity];
        }

        /// <summary>
        /// Gets the number of slots, present or not: one more than the highest identifier ever reserved.
        /// </summary>
        public int IdCount => idCount;

        /// <summary>
        /// Gets the number of present entries. Linear in <see cref="IdCount"/>.
        /// </summary>
        public int Count
        {
            get
            {
                var n = 0;
                for (var i = 0; i < idCount; i++)
                    if (present[i])
                        n++;

                return n;
            }
        }

        /// <summary>
        /// Gets whether no entry is present.
        /// </summary>
        public bool IsEmpty
        {
            get
            {
                for (var i = 0; i < idCount; i++)
                    if (present[i])
                        return false;

                return true;
            }
        }

        /// <summary>
        /// Gets the identifier the next <see cref="Push"/> would return.
        /// </summary>
        public TKey NextId => TKey.FromIndex(idCount);

        /// <summary>
        /// Gets or sets the value for <paramref name="key"/>. Getting an absent key throws.
        /// </summary>
        /// <param name="key">The key.</param>
        public TValue this[TKey key]
        {
            get
            {
                var i = key.Index;
                if ((uint)i >= (uint)idCount || present[i] == false)
                    throw new KeyNotFoundException($"No entry for {key}.");

                return values[i];
            }
            set => Insert(key, value);
        }

        /// <summary>
        /// Inserts or replaces the value for <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns>Whether a value was already present.</returns>
        public bool Insert(TKey key, TValue value)
        {
            var i = key.Index;
            ReserveSpace(i);
            var had = present[i];
            values[i] = value;
            present[i] = true;
            return had;
        }

        /// <summary>
        /// Appends a value at <see cref="NextId"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>Its identifier.</returns>
        public TKey Push(TValue value)
        {
            var i = idCount;
            ReserveSpace(i);
            values[i] = value;
            present[i] = true;
            return TKey.FromIndex(i);
        }

        /// <summary>
        /// Gets whether <paramref name="key"/> has a value.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns><c>true</c> if present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(TKey key)
        {
            var i = key.Index;
            return (uint)i < (uint)idCount && present[i];
        }

        /// <summary>
        /// Gets the value for <paramref name="key"/>, if present.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns><c>true</c> if present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(TKey key, out TValue value)
        {
            var i = key.Index;
            if ((uint)i < (uint)idCount && present[i])
            {
                value = values[i];
                return true;
            }

            value = default!;
            return false;
        }

        /// <summary>
        /// Gets a reference to the value slot for <paramref name="key"/>; the key must be present.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>A reference to the stored value.</returns>
        public ref TValue GetValueRef(TKey key)
        {
            var i = key.Index;
            if ((uint)i >= (uint)idCount || present[i] == false)
                throw new KeyNotFoundException($"No entry for {key}.");

            return ref values[i];
        }

        /// <summary>
        /// Gets the value for <paramref name="key"/>, inserting <paramref name="factory"/>'s result first if absent.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="factory">Creates the value when absent.</param>
        /// <returns>A reference to the stored value.</returns>
        public ref TValue GetOrInsert(TKey key, Func<TValue> factory)
        {
            var i = key.Index;
            ReserveSpace(i);
            if (present[i] == false)
            {
                values[i] = factory();
                present[i] = true;
            }

            return ref values[i];
        }

        /// <summary>
        /// Removes and returns the value for <paramref name="key"/>; the key must be present.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The value that was stored.</returns>
        public TValue Take(TKey key)
        {
            if (TryTake(key, out var value) == false)
                throw new KeyNotFoundException($"No entry for {key}.");

            return value;
        }

        /// <summary>
        /// Removes and returns the value for <paramref name="key"/>, if present.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value that was stored.</param>
        /// <returns><c>true</c> if a value was removed.</returns>
        public bool TryTake(TKey key, out TValue value)
        {
            var i = key.Index;
            if ((uint)i < (uint)idCount && present[i])
            {
                value = values[i];
                values[i] = default!;
                present[i] = false;
                return true;
            }

            value = default!;
            return false;
        }

        /// <summary>
        /// Removes every entry for which <paramref name="keep"/> returns <c>false</c>.
        /// </summary>
        /// <param name="keep">Decides whether an entry stays.</param>
        public void Retain(Func<TKey, TValue, bool> keep)
        {
            for (var i = 0; i < idCount; i++)
            {
                if (present[i] && keep(TKey.FromIndex(i), values[i]) == false)
                {
                    values[i] = default!;
                    present[i] = false;
                }
            }
        }

        /// <summary>
        /// Removes every entry and every slot.
        /// </summary>
        public void Clear()
        {
            Array.Clear(values, 0, idCount);
            Array.Clear(present, 0, idCount);
            idCount = 0;
        }

        /// <summary>
        /// Ensures a slot exists for index <paramref name="index"/>, growing <see cref="IdCount"/> to cover it.
        /// </summary>
        /// <param name="index">The index.</param>
        public void ReserveSpace(int index)
        {
            if (index < idCount)
                return;

            if (index >= values.Length)
            {
                var capacity = Math.Max(Math.Max(4, values.Length * 2), index + 1);
                Array.Resize(ref values, capacity);
                Array.Resize(ref present, capacity);
            }

            idCount = index + 1;
        }

        /// <summary>
        /// Creates a shallow copy.
        /// </summary>
        /// <returns>The copy.</returns>
        public DenseIdMap<TKey, TValue> Clone()
        {
            var copy = new DenseIdMap<TKey, TValue>(idCount);
            Array.Copy(values, copy.values, idCount);
            Array.Copy(present, copy.present, idCount);
            copy.idCount = idCount;
            return copy;
        }

        /// <summary>
        /// Gets an enumerator over present entries in ascending identifier order.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public DenseIdMapEnumerator<TKey, TValue> GetEnumerator() => new(values, present, idCount);

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    }

}
