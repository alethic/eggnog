using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Makes <see cref="MultiSet{T}"/>s.
    /// </summary>
    public static class MultiSet
    {

        /// <summary>
        /// Makes the multiset of the given elements, repeats counted: egglog's <c>multiset-of</c>, and the builder of a
        /// collection expression such as <c>MultiSet&lt;long&gt; m = [1, 1, 2];</c>, which <c>MultiSet.Of([1L, 1L, 2L])</c>
        /// is too. The elements are one span, not <c>params</c>, so an array is taken as its elements or refused, in every
        /// language version, and never as one element; there is deliberately no <see cref="IEnumerable{T}"/> overload
        /// named <c>Of</c>, so a string is never taken as its characters. Use <see cref="From{T}"/> for any other
        /// sequence.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="items">The elements.</param>
        /// <returns>The multiset.</returns>
        /// <exception cref="ArgumentNullException">An element is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">Two elements cannot be compared: an element type, or a type an
        /// element holds, is not comparable.</exception>
        public static MultiSet<T> Of<T>(ReadOnlySpan<T> items) where T : notnull
        {
            return MultiSet<T>.Own(items.ToArray());
        }

        /// <summary>
        /// Makes the multiset of a sequence's elements, repeats counted.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="items">The elements.</param>
        /// <returns>The multiset.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="items"/> or an element is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">Two elements cannot be compared: an element type, or a type an
        /// element holds, is not comparable.</exception>
        public static MultiSet<T> From<T>(IEnumerable<T> items) where T : notnull
        {
            ArgumentNullException.ThrowIfNull(items);
            return MultiSet<T>.Create(items);
        }

    }

}
