using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A set of values that remembers insertion order: egglog's <c>IndexSet&lt;Value&gt;</c> as the container code uses
    /// it (the ids to rebuild, the dirty ids and the frontier of their closure), which only inserts, tests, iterates in
    /// order and clears.
    /// </summary>
    /// <remarks>
    /// A list of the values in insertion order plus a hash set for membership. <see cref="Add"/> and
    /// <see cref="Contains"/> are expected O(1); <see cref="AsSpan"/> is O(1) and valid until the next change. Not
    /// thread-safe.
    /// </remarks>
    internal sealed class OrderedValueSet
    {

        readonly List<uint> order;
        readonly HashSet<uint> members;

        /// <summary>
        /// Initializes an empty set.
        /// </summary>
        public OrderedValueSet()
        {
            order = new List<uint>();
            members = new HashSet<uint>();
        }

        /// <summary>
        /// Gets the number of values.
        /// </summary>
        public int Count => order.Count;

        /// <summary>
        /// Gets whether the set is empty.
        /// </summary>
        public bool IsEmpty => order.Count == 0;

        /// <summary>
        /// Gets the value at position <paramref name="index"/> in insertion order.
        /// </summary>
        /// <param name="index">The position.</param>
        public uint this[int index] => order[index];

        /// <summary>
        /// Adds <paramref name="value"/> at the end unless it is present.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns><c>true</c> if it was added.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Add(uint value)
        {
            if (members.Add(value) == false)
                return false;

            order.Add(value);
            return true;
        }

        /// <summary>
        /// Adds every value of <paramref name="values"/>, in order, that is not present.
        /// </summary>
        /// <param name="values">The values.</param>
        public void AddRange(ReadOnlySpan<uint> values)
        {
            foreach (var value in values)
                Add(value);
        }

        /// <summary>
        /// Gets whether <paramref name="value"/> is present.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns><c>true</c> if present.</returns>
        public bool Contains(uint value) => members.Contains(value);

        /// <summary>
        /// Gets the values in insertion order. Valid until the set next changes.
        /// </summary>
        /// <returns>The values.</returns>
        public ReadOnlySpan<uint> AsSpan() => CollectionsMarshal.AsSpan(order);

        /// <summary>
        /// Copies the values, in insertion order, into a new array.
        /// </summary>
        /// <returns>The array.</returns>
        public uint[] ToArray() => order.ToArray();

        /// <summary>
        /// Removes every value, keeping the storage.
        /// </summary>
        public void Clear()
        {
            order.Clear();
            members.Clear();
        }

    }

}
