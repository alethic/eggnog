using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Eggnog
{

    /// <summary>
    /// An immutable vector of values or classes, equal by content: egglog's <c>Vec</c> container (src/sort/vec.rs),
    /// whose column type <see cref="ValueStore.RegisterVec{T}"/> registers and whose primitives are
    /// <see cref="Primitives"/>' <c>Vec*</c> methods. A vector is interned as a container, so equal vectors have equal
    /// cells, and a vector of classes is rebuilt when they merge. Operations return a new vector, or this one when nothing
    /// changes.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    public sealed class Vec<T> : IReadOnlyList<T>, IEquatable<Vec<T>>, IComparable<Vec<T>> where T : notnull
    {

        readonly T[] items;
        int hash;

        /// <summary>
        /// Initializes a vector over <paramref name="items"/>; not copied.
        /// </summary>
        Vec(T[] items)
        {
            this.items = items;
        }

        /// <summary>
        /// Gets the empty vector, egglog's <c>vec-empty</c>.
        /// </summary>
        public static Vec<T> Empty { get; } = new(Array.Empty<T>());

        /// <summary>
        /// Gets the number of elements: egglog's <c>vec-length</c>.
        /// </summary>
        public int Count => items.Length;

        /// <summary>
        /// Gets the element at <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The index, from 0.</param>
        /// <returns>The element.</returns>
        /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is out of range.</exception>
        public T this[int index] => items[index];

        /// <summary>
        /// Gets whether the vector holds <paramref name="item"/>: egglog's <c>vec-contains</c>.
        /// </summary>
        /// <param name="item">The element.</param>
        /// <returns>Whether it is held.</returns>
        public bool Contains(T item) => Array.IndexOf(items, item) >= 0;

        /// <summary>
        /// Gets the vector with <paramref name="item"/> appended: egglog's <c>vec-push</c>.
        /// </summary>
        /// <param name="item">The element.</param>
        /// <returns>The longer vector.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="item"/> is <c>null</c>.</exception>
        public Vec<T> Push(T item)
        {
            if (item is null)
                throw new ArgumentNullException(nameof(item));

            var result = new T[items.Length + 1];
            items.CopyTo(result, 0);
            result[^1] = item;
            return new(result);
        }

        /// <summary>
        /// Gets the vector without its last element: egglog's <c>vec-pop</c>, which leaves an empty vector as it is.
        /// </summary>
        /// <returns>The shorter vector; this instance when it is empty.</returns>
        public Vec<T> Pop() => items.Length == 0 ? this : new(items[..^1]);

        /// <summary>
        /// Gets the vector followed by <paramref name="other"/>: egglog's <c>vec-append</c> of two vectors.
        /// </summary>
        /// <param name="other">The vector to append.</param>
        /// <returns>The concatenation.</returns>
        public Vec<T> Append(Vec<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            if (other.items.Length == 0)
                return this;

            if (items.Length == 0)
                return other;

            return new([.. items, .. other.items]);
        }

        /// <summary>
        /// Gets the vector with the element at <paramref name="index"/> replaced: egglog's <c>vec-set</c>.
        /// </summary>
        /// <param name="index">The index, from 0.</param>
        /// <param name="item">The new element.</param>
        /// <returns>The new vector.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="item"/> is <c>null</c>.</exception>
        public Vec<T> SetItem(int index, T item)
        {
            if ((uint)index >= (uint)items.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"The vector has {items.Length} elements.");

            if (item is null)
                throw new ArgumentNullException(nameof(item));

            var result = (T[])items.Clone();
            result[index] = item;
            return new(result);
        }

        /// <summary>
        /// Gets the vector without the element at <paramref name="index"/>: egglog's <c>vec-remove</c>.
        /// </summary>
        /// <param name="index">The index, from 0.</param>
        /// <returns>The shorter vector.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
        public Vec<T> RemoveAt(int index)
        {
            if ((uint)index >= (uint)items.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"The vector has {items.Length} elements.");

            return new([.. items[..index], .. items[(index + 1)..]]);
        }

        /// <summary>
        /// Gets whether <paramref name="other"/> has the same elements in the same order.
        /// </summary>
        /// <param name="other">The other vector.</param>
        /// <returns>Whether the vectors are equal.</returns>
        public bool Equals(Vec<T>? other)
        {
            if (other is null)
                return false;

            return ReferenceEquals(this, other) || items.AsSpan().SequenceEqual(other.items, EqualityComparer<T>.Default);
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Vec<T> other && Equals(other);

        /// <summary>
        /// Compares in content order, element by element as <see cref="ValueSet{T}"/> orders its elements (by code point
        /// for strings, <c>Comparer&lt;T&gt;.Default</c> otherwise), a shorter prefix first; <c>null</c> first. Lets
        /// containers be the elements of multisets and the keys of maps.
        /// </summary>
        /// <param name="other">The other vector.</param>
        /// <returns>Negative, zero or positive.</returns>
        /// <exception cref="ArgumentException">An element type is not comparable.</exception>
        public int CompareTo(Vec<T>? other)
        {
            if (other is null)
                return 1;

            var order = ValueSetOrder<T>.Comparer;
            for (var i = 0; i < items.Length && i < other.items.Length; i++)
            {
                var c = order.Compare(items[i], other.items[i]);
                if (c != 0)
                    return c;
            }

            return items.Length.CompareTo(other.items.Length);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            if (hash == 0)
            {
                var combined = new HashCode();
                combined.Add(items.Length);
                foreach (var item in items)
                    combined.Add(item);

                var computed = combined.ToHashCode();
                hash = computed == 0 ? 1 : computed;
            }

            return hash;
        }

        /// <summary>
        /// Renders the vector as <c>[a, b]</c>, numbers in the invariant culture.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString() => "[" + string.Join(", ", items.Select(Text)) + "]";

        /// <inheritdoc />
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();

        /// <summary>
        /// Makes a vector over <paramref name="items"/>, refusing a <c>null</c> element.
        /// </summary>
        /// <param name="items">The elements; owned by the vector from now on.</param>
        /// <returns>The vector.</returns>
        /// <exception cref="ArgumentNullException">An element is <c>null</c>.</exception>
        internal static Vec<T> Own(T[] items)
        {
            if (items.Length == 0)
                return Empty;

            foreach (var item in items)
                if (item is null)
                    throw new ArgumentNullException(nameof(items), "A vector's element is null.");

            return new(items);
        }

        /// <summary>
        /// Formats an element in the invariant culture.
        /// </summary>
        static string Text(T item) => item is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : item.ToString() ?? string.Empty;

    }

}
