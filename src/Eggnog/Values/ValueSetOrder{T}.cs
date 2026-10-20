using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The element order of <see cref="ValueSet{T}"/>: ordinal for strings, egglog's <c>OrderedFloat</c> for doubles
    /// (<see cref="OrderedDouble"/>, NaN last), <c>Comparer&lt;T&gt;.Default</c> otherwise. Culture-invariant for the
    /// built-in types, so equal sets have one sorted form and one interned cell. The <c>Max</c> and <c>Min</c> lattices
    /// order by it too.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal static class ValueSetOrder<T>
    {

        /// <summary>
        /// Gets the comparer.
        /// </summary>
        public static IComparer<T> Comparer { get; } = typeof(T) == typeof(string)
            ? (IComparer<T>)(object)StringComparer.Ordinal
            : typeof(T) == typeof(double)
                ? (IComparer<T>)(object)OrderedDouble.Instance
                : Comparer<T>.Default;

    }

}
