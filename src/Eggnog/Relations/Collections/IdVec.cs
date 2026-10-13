using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A dense vector indexed by a numeric identifier, where every slot below <see cref="Count"/> holds a value.
    /// </summary>
    /// <typeparam name="TKey">The identifier type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    internal sealed class IdVec<TKey, TValue>
        where TKey : struct, INumericId<TKey>
    {

        TValue[] values;
        int count;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public IdVec()
        {
            values = Array.Empty<TValue>();
        }

        /// <summary>
        /// Initializes a new instance with room for <paramref name="capacity"/> values.
        /// </summary>
        /// <param name="capacity">The capacity.</param>
        public IdVec(int capacity)
        {
            values = capacity == 0 ? Array.Empty<TValue>() : new TValue[capacity];
        }

        /// <summary>
        /// Gets the number of slots.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets a reference to the value at <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The identifier.</param>
        public ref TValue this[TKey key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var i = key.Index;
                if ((uint)i >= (uint)count)
                    throw new ArgumentOutOfRangeException(nameof(key));

                return ref values[i];
            }
        }

        /// <summary>
        /// Appends a value.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>Its identifier.</returns>
        public TKey Push(TValue value)
        {
            if (count == values.Length)
                Array.Resize(ref values, Math.Max(4, values.Length * 2));

            values[count] = value;
            return TKey.FromIndex(count++);
        }

        /// <summary>
        /// Grows the vector so that <paramref name="key"/> is in range, filling new slots with
        /// <paramref name="factory"/>, and returns a reference to its slot.
        /// </summary>
        /// <param name="key">The identifier.</param>
        /// <param name="factory">Creates the value for each new slot.</param>
        /// <returns>A reference to the slot.</returns>
        public ref TValue GetOrResize(TKey key, Func<TValue> factory)
        {
            var i = key.Index;
            if (i >= count)
            {
                if (i >= values.Length)
                    Array.Resize(ref values, Math.Max(Math.Max(4, values.Length * 2), i + 1));

                for (var j = count; j <= i; j++)
                    values[j] = factory();

                count = i + 1;
            }

            return ref values[i];
        }

        /// <summary>
        /// Gets the populated slots.
        /// </summary>
        /// <returns>A span over the slots.</returns>
        public Span<TValue> AsSpan() => values.AsSpan(0, count);

        /// <summary>
        /// Removes every slot.
        /// </summary>
        public void Clear()
        {
            Array.Clear(values, 0, count);
            count = 0;
        }

    }

}
