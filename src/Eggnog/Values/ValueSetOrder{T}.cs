using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// The element order of <see cref="ValueSet{T}"/>: by code point for strings, as Rust's
    /// (<see cref="Relations.CodePointComparer"/>), egglog's <c>OrderedFloat</c> for doubles (<see cref="OrderedDouble"/>,
    /// NaN last), by cell for the classes of a sort (<see cref="ClassOrder{TSort}"/>, set when the sort is defined),
    /// <c>Comparer&lt;T&gt;.Default</c> otherwise. Culture-invariant for the built-in types, so equal sets have one
    /// sorted form and one interned cell. Maps, multisets and the <c>Max</c> and <c>Min</c> lattices order by it too.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal static class ValueSetOrder<T>
    {

        static IComparer<T> comparer = typeof(T) == typeof(string)
            ? (IComparer<T>)(object)Relations.CodePointComparer.Instance
            : typeof(T) == typeof(double)
                ? (IComparer<T>)(object)OrderedDouble.Instance
                : Comparer<T>.Default;

        /// <summary>
        /// Gets the comparer.
        /// </summary>
        public static IComparer<T> Comparer => Volatile.Read(ref comparer);

        /// <summary>
        /// Orders the values by <paramref name="order"/> from now on: the classes of a sort by cell, when the sort is
        /// defined, since a sort's type knows no order of its own.
        /// </summary>
        /// <param name="order">The order.</param>
        public static void Use(IComparer<T> order) => Volatile.Write(ref comparer, order);

    }

}
