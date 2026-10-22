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
        /// Makes the vector of the given elements, in order: egglog's <c>vec-of</c>. There is deliberately no
        /// <see cref="IEnumerable{T}"/> overload named <c>Of</c>, so a string is never taken as its characters; use
        /// <see cref="From{T}"/> for a sequence.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="items">The elements.</param>
        /// <returns>The vector.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="items"/> or an element is <c>null</c>.</exception>
        public static Vec<T> Of<T>(params T[] items) where T : notnull
        {
            ArgumentNullException.ThrowIfNull(items);
            return Vec<T>.Own((T[])items.Clone());
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
