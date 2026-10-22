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
        /// Makes the set of the given elements: <c>Set.Of("abc")</c> is <c>{"abc"}</c>. There is deliberately no
        /// <see cref="IEnumerable{T}"/> overload named <c>Of</c>, so a string is never taken as its characters; use
        /// <see cref="From{T}"/> for a sequence.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="items">The elements; duplicates are dropped.</param>
        /// <returns>The set.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="items"/> or an element is <c>null</c>.</exception>
        public static Set<T> Of<T>(params T[] items) where T : notnull
        {
            ArgumentNullException.ThrowIfNull(items);
            return Set<T>.Create(items);
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
