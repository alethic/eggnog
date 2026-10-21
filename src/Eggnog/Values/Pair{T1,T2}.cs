using System;
using System.Collections.Generic;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// A pair of values or classes: egglog's <c>Pair</c> container (src/sort/pair.rs), whose column type
    /// <see cref="ValueStore.RegisterPair{T1, T2}"/> registers and whose primitives are <see cref="Primitives.PairOf{T1, T2}"/>,
    /// <see cref="Primitives.PairFirst{T1, T2}"/> and <see cref="Primitives.PairSecond{T1, T2}"/>. A pair is interned as a container,
    /// so equal pairs have equal cells. Immutable.
    /// </summary>
    /// <typeparam name="T1">The type of the first element.</typeparam>
    /// <typeparam name="T2">The type of the second element.</typeparam>
    public readonly struct Pair<T1, T2> : IEquatable<Pair<T1, T2>>, IComparable<Pair<T1, T2>>, IPair
        where T1 : notnull
        where T2 : notnull
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="first">The first element.</param>
        /// <param name="second">The second element.</param>
        /// <exception cref="ArgumentNullException">An element is <c>null</c>.</exception>
        public Pair(T1 first, T2 second)
        {
            if (first is null)
                throw new ArgumentNullException(nameof(first));

            if (second is null)
                throw new ArgumentNullException(nameof(second));

            First = first;
            Second = second;
        }

        /// <summary>
        /// Gets the first element: egglog's <c>pair-first</c>.
        /// </summary>
        public T1 First { get; }

        /// <summary>
        /// Gets the second element: egglog's <c>pair-second</c>.
        /// </summary>
        public T2 Second { get; }

        /// <inheritdoc />
        object IPair.FirstValue => First;

        /// <inheritdoc />
        object IPair.SecondValue => Second;

        /// <summary>
        /// Gets the elements.
        /// </summary>
        /// <param name="first">The first element.</param>
        /// <param name="second">The second element.</param>
        public void Deconstruct(out T1 first, out T2 second)
        {
            first = First;
            second = Second;
        }

        /// <inheritdoc />
        public bool Equals(Pair<T1, T2> other) =>
            EqualityComparer<T1>.Default.Equals(First, other.First) && EqualityComparer<T2>.Default.Equals(Second, other.Second);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Pair<T1, T2> other && Equals(other);

        /// <summary>
        /// Compares in content order, element by element as <see cref="ValueSet{T}"/> orders its elements (by code point
        /// for strings, <c>Comparer&lt;T&gt;.Default</c> otherwise), the first element then the second. Lets containers
        /// be the elements of multisets and the keys of maps.
        /// </summary>
        /// <param name="other">The other pair.</param>
        /// <returns>Negative, zero or positive.</returns>
        /// <exception cref="ArgumentException">An element type is not comparable.</exception>
        public int CompareTo(Pair<T1, T2> other)
        {
            var order = ValueSetOrder<T1>.Comparer.Compare(First, other.First);
            return order != 0 ? order : ValueSetOrder<T2>.Comparer.Compare(Second, other.Second);
        }

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(First, Second);

        /// <summary>
        /// Renders the pair as <c>(first, second)</c>, numbers in the invariant culture.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString() => $"({Text(First)}, {Text(Second)})";

        /// <summary>
        /// Gets whether two pairs are equal.
        /// </summary>
        /// <param name="left">One pair.</param>
        /// <param name="right">The other pair.</param>
        /// <returns>Whether they are equal.</returns>
        public static bool operator ==(Pair<T1, T2> left, Pair<T1, T2> right) => left.Equals(right);

        /// <summary>
        /// Gets whether two pairs differ.
        /// </summary>
        /// <param name="left">One pair.</param>
        /// <param name="right">The other pair.</param>
        /// <returns>Whether they differ.</returns>
        public static bool operator !=(Pair<T1, T2> left, Pair<T1, T2> right) => left.Equals(right) == false;

        /// <summary>
        /// Formats an element in the invariant culture.
        /// </summary>
        static string Text(object? value) => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    }

}
