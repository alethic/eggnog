using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Eggnog
{

    /// <summary>
    /// An immutable multiset of values or classes, equal by content: egglog's <c>MultiSet</c> container
    /// (src/sort/multiset.rs), whose column type <see cref="ValueStore.RegisterMultiSet{T}"/> registers and whose
    /// primitives are <see cref="Primitives"/>' <c>MultiSet*</c> methods. A multiset is interned as a container, so equal
    /// multisets have equal cells. It enumerates its elements with their repeats in ascending element order, as
    /// <see cref="Set{T}"/> orders its elements, a sort's classes by cell, so the element type must be
    /// <c>string</c>, comparable or a sort; egglog orders
    /// a multiset by its elements' cells, which is the order its terms and serialized nodes list. Its counts are
    /// <c>long</c>s, as egglog's are <c>usize</c>s; as an <see cref="IReadOnlyCollection{T}"/> it counts to
    /// <see cref="int.MaxValue"/>. Operations return a new multiset, or this one when nothing changes.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    public sealed class MultiSet<T> : IReadOnlyCollection<T>, IEquatable<MultiSet<T>>, IComparable<MultiSet<T>>
        where T : notnull
    {

        static IComparer<T> Order => ElementOrder<T>.Comparer;

        readonly T[] elements;
        readonly long[] counts;
        int hash;

        /// <summary>
        /// Initializes a multiset over distinct elements in ascending order with positive counts; not copied.
        /// </summary>
        MultiSet(T[] elements, long[] counts, long count)
        {
            this.elements = elements;
            this.counts = counts;
            Count = count;
        }

        /// <summary>
        /// Gets the empty multiset.
        /// </summary>
        public static MultiSet<T> Empty { get; } = new(Array.Empty<T>(), Array.Empty<long>(), 0);

        /// <summary>
        /// Gets the number of elements, counted with their repeats: egglog's <c>multiset-length</c>.
        /// </summary>
        public long Count { get; }

        /// <summary>
        /// Gets the number of elements, counted with their repeats.
        /// </summary>
        /// <exception cref="OverflowException">The multiset has more than <see cref="int.MaxValue"/>
        /// elements.</exception>
        int IReadOnlyCollection<T>.Count => checked((int)Count);

        /// <summary>
        /// Gets the distinct elements, ascending, each with its count.
        /// </summary>
        public IEnumerable<KeyValuePair<T, long>> Counts
        {
            get
            {
                for (var i = 0; i < elements.Length; i++)
                    yield return new KeyValuePair<T, long>(elements[i], counts[i]);
            }
        }

        /// <summary>
        /// Gets how many times <paramref name="item"/> occurs: egglog's <c>multiset-count</c>.
        /// </summary>
        /// <param name="item">The element.</param>
        /// <returns>Its count; 0 when it does not occur.</returns>
        public long CountOf(T item)
        {
            var at = IndexOf(item);
            return at >= 0 ? counts[at] : 0;
        }

        /// <summary>
        /// Gets whether <paramref name="item"/> occurs: egglog's <c>multiset-contains</c>.
        /// </summary>
        /// <param name="item">The element.</param>
        /// <returns>Whether it occurs.</returns>
        public bool Contains(T item) => IndexOf(item) >= 0;

        /// <summary>
        /// Gets the multiset with one more <paramref name="item"/>: egglog's <c>multiset-insert</c>.
        /// </summary>
        /// <param name="item">The element.</param>
        /// <returns>The new multiset.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="item"/> is <c>null</c>.</exception>
        public MultiSet<T> Insert(T item) => Add(item, 1);

        /// <summary>
        /// Gets the multiset with one <paramref name="item"/> fewer: egglog's <c>multiset-remove</c>, which has no result
        /// when the item does not occur.
        /// </summary>
        /// <param name="item">The element.</param>
        /// <returns>The new multiset; this instance when the item does not occur.</returns>
        public MultiSet<T> Remove(T item)
        {
            var at = IndexOf(item);
            if (at < 0)
                return this;

            if (counts[at] > 1)
            {
                var fewer = (long[])counts.Clone();
                fewer[at]--;
                return new(elements, fewer, Count - 1);
            }

            return new([.. elements[..at], .. elements[(at + 1)..]], [.. counts[..at], .. counts[(at + 1)..]], Count - 1);
        }

        /// <summary>
        /// Subtracts the counts of <paramref name="other"/>: egglog's <c>multiset-subtract</c>, which has no result when
        /// an element of <paramref name="other"/> occurs fewer times here.
        /// </summary>
        /// <param name="other">The multiset to subtract.</param>
        /// <param name="result">The difference, when the result is <c>true</c>.</param>
        /// <returns>Whether every element of <paramref name="other"/> occurs here at least as often.</returns>
        public bool TrySubtract(MultiSet<T> other, out MultiSet<T> result)
        {
            ArgumentNullException.ThrowIfNull(other);
            var left = new Dictionary<int, long>();
            for (var i = 0; i < other.elements.Length; i++)
            {
                var at = IndexOf(other.elements[i]);
                if (at < 0 || counts[at] < other.counts[i])
                {
                    result = this;
                    return false;
                }

                left[at] = counts[at] - other.counts[i];
            }

            var newElements = new List<T>(elements.Length);
            var newCounts = new List<long>(elements.Length);
            for (var i = 0; i < elements.Length; i++)
            {
                var count = left.TryGetValue(i, out var reduced) ? reduced : counts[i];
                if (count > 0)
                {
                    newElements.Add(elements[i]);
                    newCounts.Add(count);
                }
            }

            result = new(newElements.ToArray(), newCounts.ToArray(), Count - other.Count);
            return true;
        }

        /// <summary>
        /// Gets the sum of the multisets, each element's counts added: egglog's <c>multiset-sum</c>.
        /// </summary>
        /// <param name="other">The other multiset.</param>
        /// <returns>The sum.</returns>
        public MultiSet<T> Sum(MultiSet<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            if (other.Count == 0)
                return this;

            if (Count == 0)
                return other;

            return Merge(other, static (a, b) => checked(a + b), keepMissing: true);
        }

        /// <summary>
        /// Gets the intersection of the multisets, each element's count the lesser of its counts: egglog's
        /// <c>multiset-intersection</c>.
        /// </summary>
        /// <param name="other">The other multiset.</param>
        /// <returns>The intersection.</returns>
        public MultiSet<T> Intersect(MultiSet<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);
            return Merge(other, static (a, b) => Math.Min(a, b), keepMissing: false);
        }

        /// <summary>
        /// Gets the multiset with every count set to one: egglog's <c>multiset-reset-counts</c>.
        /// </summary>
        /// <returns>The multiset of the distinct elements.</returns>
        public MultiSet<T> ResetCounts()
        {
            if (Count == elements.Length)
                return this;

            var ones = new long[elements.Length];
            Array.Fill(ones, 1);
            return new(elements, ones, elements.Length);
        }

        /// <summary>
        /// Gets whether <paramref name="other"/> has the same elements with the same counts.
        /// </summary>
        /// <param name="other">The other multiset.</param>
        /// <returns>Whether the multisets are equal.</returns>
        public bool Equals(MultiSet<T>? other)
        {
            if (other is null)
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return Count == other.Count
                && counts.AsSpan().SequenceEqual(other.counts)
                && elements.AsSpan().SequenceEqual(other.elements, EqualityComparer<T>.Default);
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is MultiSet<T> other && Equals(other);

        /// <summary>
        /// Compares in content order, element by element as <see cref="Set{T}"/> orders its elements (by code point
        /// for strings, <c>Comparer&lt;T&gt;.Default</c> otherwise), its elements with their repeats in element order,
        /// a shorter prefix first; <c>null</c> first. Lets containers be the elements of multisets and the keys of maps.
        /// </summary>
        /// <param name="other">The other multiset.</param>
        /// <returns>Negative, zero or positive.</returns>
        /// <exception cref="ArgumentException">An element type is not comparable.</exception>
        public int CompareTo(MultiSet<T>? other)
        {
            if (other is null)
                return 1;

            using var left = GetEnumerator();
            using var right = other.GetEnumerator();
            while (true)
            {
                var hasLeft = left.MoveNext();
                var hasRight = right.MoveNext();
                if (hasLeft == false || hasRight == false)
                    return hasLeft.CompareTo(hasRight);

                var c = Order.Compare(left.Current, right.Current);
                if (c != 0)
                    return c;
            }
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            if (hash == 0)
            {
                var combined = new HashCode();
                combined.Add(Count);
                for (var i = 0; i < elements.Length; i++)
                {
                    combined.Add(elements[i]);
                    combined.Add(counts[i]);
                }

                var computed = combined.ToHashCode();
                hash = computed == 0 ? 1 : computed;
            }

            return hash;
        }

        /// <summary>
        /// Renders the multiset as <c>{a, a, b}</c>, its elements with their repeats in element order, numbers in the
        /// invariant culture.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString() => "{" + string.Join(", ", this.Select(Text)) + "}";

        /// <inheritdoc />
        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < elements.Length; i++)
                for (var k = 0L; k < counts[i]; k++)
                    yield return elements[i];
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Makes a multiset of elements in any order, repeats counted.
        /// </summary>
        /// <param name="items">The elements.</param>
        /// <returns>The multiset.</returns>
        /// <exception cref="ArgumentNullException">An element is <c>null</c>.</exception>
        internal static MultiSet<T> Create(IEnumerable<T> items)
        {
            var sorted = items.ToArray();
            if (sorted.Length == 0)
                return Empty;

            foreach (var item in sorted)
                if (item is null)
                    throw new ArgumentNullException(nameof(items), "A multiset's element is null.");

            Array.Sort(sorted, Order);
            var distinct = new List<T>(sorted.Length);
            var counts = new List<long>(sorted.Length);
            foreach (var item in sorted)
            {
                if (distinct.Count > 0 && Order.Compare(distinct[^1], item) == 0)
                {
                    counts[^1]++;
                    continue;
                }

                distinct.Add(item);
                counts.Add(1);
            }

            return new(distinct.ToArray(), counts.ToArray(), sorted.Length);
        }

        /// <summary>
        /// Makes a multiset of distinct elements with positive counts, in any order.
        /// </summary>
        /// <param name="entries">The elements and their counts.</param>
        /// <returns>The multiset.</returns>
        internal static MultiSet<T> FromCounts(IReadOnlyList<KeyValuePair<T, long>> entries)
        {
            if (entries.Count == 0)
                return Empty;

            var sorted = entries.OrderBy(e => e.Key, Order).ToArray();
            var total = 0L;
            foreach (var entry in sorted)
                total = checked(total + entry.Value);

            return new(sorted.Select(e => e.Key).ToArray(), sorted.Select(e => e.Value).ToArray(), total);
        }

        /// <summary>
        /// Makes a multiset of elements with positive counts, in any order, the counts of equal elements added.
        /// </summary>
        /// <param name="entries">The elements and their counts.</param>
        /// <returns>The multiset.</returns>
        internal static MultiSet<T> AddCounts(IReadOnlyList<KeyValuePair<T, long>> entries)
        {
            if (entries.Count == 0)
                return Empty;

            var sorted = entries.OrderBy(e => e.Key, Order).ToArray();
            var distinct = new List<T>(sorted.Length);
            var counts = new List<long>(sorted.Length);
            var total = 0L;
            foreach (var (item, count) in sorted)
            {
                total = checked(total + count);
                if (distinct.Count > 0 && Order.Compare(distinct[^1], item) == 0)
                {
                    counts[^1] = checked(counts[^1] + count);
                    continue;
                }

                distinct.Add(item);
                counts.Add(count);
            }

            return new(distinct.ToArray(), counts.ToArray(), total);
        }

        /// <summary>
        /// Adds <paramref name="count"/> occurrences of <paramref name="item"/>.
        /// </summary>
        MultiSet<T> Add(T item, long count)
        {
            if (item is null)
                throw new ArgumentNullException(nameof(item));

            var at = IndexOf(item);
            if (at >= 0)
            {
                var more = (long[])counts.Clone();
                more[at] = checked(more[at] + count);
                return new(elements, more, checked(Count + count));
            }

            var insert = ~at;
            return new(
                [.. elements[..insert], item, .. elements[insert..]],
                [.. counts[..insert], count, .. counts[insert..]],
                checked(Count + count));
        }

        /// <summary>
        /// Merges two multisets element by element: <paramref name="combine"/> over the counts of an element in both, and
        /// an element in one only kept when <paramref name="keepMissing"/>.
        /// </summary>
        MultiSet<T> Merge(MultiSet<T> other, Func<long, long, long> combine, bool keepMissing)
        {
            var newElements = new List<T>(elements.Length + other.elements.Length);
            var newCounts = new List<long>(elements.Length + other.elements.Length);
            int i = 0, j = 0;
            while (i < elements.Length || j < other.elements.Length)
            {
                var order = i == elements.Length ? 1
                    : j == other.elements.Length ? -1
                    : Order.Compare(elements[i], other.elements[j]);
                if (order == 0)
                {
                    var count = combine(counts[i], other.counts[j]);
                    if (count > 0)
                    {
                        newElements.Add(elements[i]);
                        newCounts.Add(count);
                    }

                    i++;
                    j++;
                }
                else if (order < 0)
                {
                    if (keepMissing)
                    {
                        newElements.Add(elements[i]);
                        newCounts.Add(counts[i]);
                    }

                    i++;
                }
                else
                {
                    if (keepMissing)
                    {
                        newElements.Add(other.elements[j]);
                        newCounts.Add(other.counts[j]);
                    }

                    j++;
                }
            }

            var total = 0L;
            foreach (var count in newCounts)
                total = checked(total + count);

            return newElements.Count == 0 ? Empty : new(newElements.ToArray(), newCounts.ToArray(), total);
        }

        /// <summary>
        /// Finds an element by binary search.
        /// </summary>
        /// <returns>Its index, or the complement of where it would be inserted.</returns>
        int IndexOf(T item)
        {
            if (item is null)
                throw new ArgumentNullException(nameof(item));

            return Array.BinarySearch(elements, item, Order);
        }

        /// <summary>
        /// Formats an element in the invariant culture.
        /// </summary>
        static string Text(T item) => item is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : item.ToString() ?? string.Empty;

    }

}
