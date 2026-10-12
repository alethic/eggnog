using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The element order of <see cref="ValueSet{T}"/>: ordinal for strings, <c>Comparer&lt;T&gt;.Default</c> otherwise.
    /// Culture-invariant for the built-in types, so equal sets have one sorted form and one interned cell.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal static class ValueSetOrder<T>
    {

        /// <summary>
        /// Gets the comparer.
        /// </summary>
        public static IComparer<T> Comparer { get; } = typeof(T) == typeof(string)
            ? (IComparer<T>)(object)StringComparer.Ordinal
            : Comparer<T>.Default;

    }

}
