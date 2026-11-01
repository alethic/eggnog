using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Makes <see cref="Set{T}"/>s.
    /// </summary>
    public static class Set
    {

        /// <summary>
        /// Makes the set of the given elements: <c>Set.Of(["abc"])</c> is <c>{"abc"}</c>, and so is the collection
        /// expression <c>Set&lt;string&gt; s = ["abc"];</c>. The elements are one span, not <c>params</c>, so an array is
        /// taken as its elements or refused, in every language version, and never as one element; there is deliberately
        /// no <see cref="IEnumerable{T}"/> overload named <c>Of</c>, so a string is never taken as its characters. Use
        /// <see cref="From{T}"/> for any other sequence.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="items">The elements; duplicates are dropped.</param>
        /// <returns>The set.</returns>
        /// <exception cref="ArgumentNullException">An element is <c>null</c>.</exception>
        public static Set<T> Of<T>(ReadOnlySpan<T> items) where T : notnull
        {
            return Set<T>.Own(items.ToArray());
        }

        /// <summary>
        /// Makes the set of a sequence's elements.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="items">The elements; duplicates are dropped.</param>
        /// <returns>The set.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="items"/> or an element is <c>null</c>.</exception>
        public static Set<T> From<T>(IEnumerable<T> items) where T : notnull
        {
            ArgumentNullException.ThrowIfNull(items);
            return Set<T>.Create(items);
        }

    }

}
