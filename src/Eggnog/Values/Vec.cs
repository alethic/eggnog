using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog
{

    /// <summary>
    /// Makes <see cref="Vec{T}"/>s.
    /// </summary>
    public static class Vec
    {

        /// <summary>
        /// Makes the vector of the given elements, in order: egglog's <c>vec-of</c>, and the builder of a collection
        /// expression such as <c>Vec&lt;long&gt; v = [1, 2, 3];</c>, which <c>Vec.Of([1L, 2L, 3L])</c> is too. The elements
        /// are one span, not <c>params</c>, so an array is taken as its elements or refused, in every language version,
        /// and never as one element; there is deliberately no <see cref="IEnumerable{T}"/> overload named <c>Of</c>, so a
        /// string is never taken as its characters. Use <see cref="From{T}"/> for any other sequence.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="items">The elements.</param>
        /// <returns>The vector.</returns>
        /// <exception cref="ArgumentNullException">An element is <c>null</c>.</exception>
        public static Vec<T> Of<T>(ReadOnlySpan<T> items) where T : notnull
        {
            return Vec<T>.Own(items.ToArray());
        }

        /// <summary>
        /// Makes the vector of a sequence's elements, in order.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="items">The elements.</param>
        /// <returns>The vector.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="items"/> or an element is <c>null</c>.</exception>
        public static Vec<T> From<T>(IEnumerable<T> items) where T : notnull
        {
            ArgumentNullException.ThrowIfNull(items);
            return Vec<T>.Own(items.ToArray());
        }

    }

}
