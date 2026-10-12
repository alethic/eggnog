using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Eggnog
{

    /// <summary>
    /// An immutable set of values in ascending element order (ordinal for strings, <c>Comparer&lt;T&gt;.Default</c>
    /// otherwise), equal by content. Operations return this instance when nothing changes.
    /// </summary>
    /// <remarks>
    /// A set is an interned base value, not a container (D20): its cells are never canonicalized or rewritten, and sets
    /// hold values only, never classes. Equality is element-wise; the hash combines the element hashes in order; because
    /// elements are kept sorted, equal sets have one interned cell. Lookups are binary searches; set operations are
    /// linear merges.
    /// </remarks>
    /// <typeparam name="T">The element type.</typeparam>
    public sealed class ValueSet<T> : IReadOnlyList<T>, IEquatable<ValueSet<T>> where T : notnull
    {

        static readonly IComparer<T> Order = ValueSetOrder<T>.Comparer;

        readonly T[] items;
        int hash;

        /// <summary>
        /// Initializes a set over <paramref name="sorted"/>, which is sorted and free of duplicates; not copied.
        /// </summary>
        ValueSet(T[] sorted)
        {
            items = sorted;
        }

        /// <summary>
        /// Gets the empty set.
        /// </summary>
        public static ValueSet<T> Empty { get; } = new(Array.Empty<T>());

        /// <summary>
        /// Gets the number of elements.
        /// </summary>
        public int Count => items.Length;

        /// <summary>
        /// Gets whether the set has no element.
        /// </summary>
        public bool IsEmpty => items.Length == 0;

        /// <summary>
        /// Gets the element at <paramref name="index"/> in ascending order.
        /// </summary>
        /// <param name="index">The index, from 0.</param>
        /// <returns>The element.</returns>
        public T this[int index] => items[index];

        /// <summary>
        /// Gets whether <paramref name="item"/> is an element. O(log n).
        /// </summary>
        /// <param name="item">The value.</param>
        /// <returns>Whether it is an element.</returns>
        public bool Contains(T item)
        {
            ArgumentNullException.ThrowIfNull(item);
            return Array.BinarySearch(items, item, Order) >= 0;
        }

        /// <summary>
        /// Gets whether every element is an element of <paramref name="other"/>.
        /// </summary>
        /// <param name="other">The other set.</param>
        /// <returns>Whether this set is a subset of it.</returns>
        public bool IsSubsetOf(ValueSet<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            if (items.Length > other.items.Length)
                return false;

            var j = 0;
            foreach (var item in items)
            {
                while (j < other.items.Length && Order.Compare(other.items[j], item) < 0)
                    j++;

                if (j == other.items.Length || Order.Compare(other.items[j], item) != 0)
                    return false;

                j++;
            }

            return true;
        }

        /// <summary>
        /// Gets whether the sets share an element.
        /// </summary>
        /// <param name="other">The other set.</param>
        /// <returns>Whether they overlap.</returns>
        public bool Overlaps(ValueSet<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            int i = 0, j = 0;
            while (i < items.Length && j < other.items.Length)
            {
                var order = Order.Compare(items[i], other.items[j]);
                if (order == 0)
                    return true;

                if (order < 0)
                    i++;
                else
                    j++;
            }

            return false;
        }

        /// <summary>
        /// Gets the set with <paramref name="item"/> added.
        /// </summary>
        /// <param name="item">The value.</param>
        /// <returns>The set; this instance when <paramref name="item"/> is an element already.</returns>
        public ValueSet<T> Add(T item)
        {
            ArgumentNullException.ThrowIfNull(item);
            var index = Array.BinarySearch(items, item, Order);
            if (index >= 0)
                return this;

            index = ~index;
            var result = new T[items.Length + 1];
            Array.Copy(items, result, index);
            result[index] = item;
            Array.Copy(items, index, result, index + 1, items.Length - index);
            return new ValueSet<T>(result);
        }

        /// <summary>
        /// Gets the set with <paramref name="item"/> removed.
        /// </summary>
        /// <param name="item">The value.</param>
        /// <returns>The set; this instance when <paramref name="item"/> is not an element.</returns>
        public ValueSet<T> Remove(T item)
        {
            ArgumentNullException.ThrowIfNull(item);
            var index = Array.BinarySearch(items, item, Order);
            if (index < 0)
                return this;

            if (items.Length == 1)
                return Empty;

            var result = new T[items.Length - 1];
            Array.Copy(items, result, index);
            Array.Copy(items, index + 1, result, index, items.Length - index - 1);
            return new ValueSet<T>(result);
        }

        /// <summary>
        /// Gets the union of the sets.
        /// </summary>
        /// <param name="other">The other set.</param>
        /// <returns>The union; this instance when <paramref name="other"/> adds nothing.</returns>
        public ValueSet<T> Union(ValueSet<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            if (other.IsSubsetOf(this))
                return this;

            if (IsSubsetOf(other))
                return other;

            var result = new List<T>(items.Length + other.items.Length);
            int i = 0, j = 0;
            while (i < items.Length || j < other.items.Length)
            {
                if (j == other.items.Length || (i < items.Length && Order.Compare(items[i], other.items[j]) < 0))
                {
                    result.Add(items[i++]);
                }
                else if (i == items.Length || Order.Compare(items[i], other.items[j]) > 0)
                {
                    result.Add(other.items[j++]);
                }
                else
                {
                    result.Add(items[i++]);
                    j++;
                }
            }

            return new ValueSet<T>(result.ToArray());
        }

        /// <summary>
        /// Gets the intersection of the sets.
        /// </summary>
        /// <param name="other">The other set.</param>
        /// <returns>The intersection; this instance when every element is in <paramref name="other"/>.</returns>
        public ValueSet<T> Intersect(ValueSet<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            if (IsSubsetOf(other))
                return this;

            var result = new List<T>(Math.Min(items.Length, other.items.Length));
            int i = 0, j = 0;
            while (i < items.Length && j < other.items.Length)
            {
                var order = Order.Compare(items[i], other.items[j]);
                if (order == 0)
                {
                    result.Add(items[i]);
                    i++;
                    j++;
                }
                else if (order < 0)
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }

            return result.Count == 0 ? Empty : new ValueSet<T>(result.ToArray());
        }

        /// <summary>
        /// Gets the elements that are not in <paramref name="other"/>.
        /// </summary>
        /// <param name="other">The other set.</param>
        /// <returns>The difference; this instance when the sets do not overlap.</returns>
        public ValueSet<T> Except(ValueSet<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            if (Overlaps(other) == false)
                return this;

            var result = new List<T>(items.Length);
            var j = 0;
            foreach (var item in items)
            {
                while (j < other.items.Length && Order.Compare(other.items[j], item) < 0)
                    j++;

                if (j == other.items.Length || Order.Compare(other.items[j], item) != 0)
                    result.Add(item);
            }

            return result.Count == 0 ? Empty : new ValueSet<T>(result.ToArray());
        }

        /// <summary>
        /// Gets the elements that satisfy <paramref name="predicate"/>.
        /// </summary>
        /// <param name="predicate">The test.</param>
        /// <returns>The filtered set; this instance when every element passes.</returns>
        public ValueSet<T> Where(Func<T, bool> predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            List<T>? result = null;
            for (var i = 0; i < items.Length; i++)
            {
                if (predicate(items[i]))
                {
                    result?.Add(items[i]);
                }
                else if (result is null)
                {
                    result = new List<T>(items.Length);
                    for (var k = 0; k < i; k++)
                        result.Add(items[k]);
                }
            }

            if (result is null)
                return this;

            return result.Count == 0 ? Empty : new ValueSet<T>(result.ToArray());
        }

        /// <summary>
        /// Gets whether <paramref name="other"/> has the same elements.
        /// </summary>
        /// <param name="other">The other set.</param>
        /// <returns>Whether the sets are equal.</returns>
        public bool Equals(ValueSet<T>? other)
        {
            if (other is null)
                return false;

            if (ReferenceEquals(this, other))
                return true;

            if (items.Length != other.items.Length)
                return false;

            var comparer = EqualityComparer<T>.Default;
            for (var i = 0; i < items.Length; i++)
                if (comparer.Equals(items[i], other.items[i]) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ValueSet<T> other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var cached = hash;
            if (cached != 0)
                return cached;

            var combined = new HashCode();
            combined.Add(items.Length);
            foreach (var item in items)
                combined.Add(item);

            cached = combined.ToHashCode();
            if (cached == 0)
                cached = 1;

            hash = cached;
            return cached;
        }

        /// <summary>
        /// Enumerates the elements in ascending order.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();

        /// <summary>
        /// Renders the set as <c>{a, b}</c>, elements formatted culture-invariantly.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString()
        {
            var builder = new StringBuilder("{");
            for (var i = 0; i < items.Length; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(items[i] is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : items[i].ToString());
            }

            return builder.Append('}').ToString();
        }

        /// <summary>
        /// Makes a set of <paramref name="source"/>'s elements, sorting them and dropping duplicates.
        /// </summary>
        /// <param name="source">The elements.</param>
        /// <returns>The set.</returns>
        /// <exception cref="ArgumentNullException">An element is <c>null</c>.</exception>
        internal static ValueSet<T> Create(IEnumerable<T> source)
        {
            var list = new List<T>(source);
            if (list.Count == 0)
                return Empty;

            foreach (var item in list)
                if (item is null)
                    throw new ArgumentNullException(nameof(source), "A set cannot hold null.");

            list.Sort(Order);
            var distinct = 1;
            for (var i = 1; i < list.Count; i++)
                if (Order.Compare(list[i], list[distinct - 1]) != 0)
                    list[distinct++] = list[i];

            var result = new T[distinct];
            list.CopyTo(0, result, 0, distinct);
            return new ValueSet<T>(result);
        }

    }

}
