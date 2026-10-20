using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The generic primitives of <see cref="Primitives"/> for one type, made once per <typeparamref name="T"/>: the set
    /// primitives over its sets, and <c>bool-!=</c> over its values.
    /// </summary>
    /// <typeparam name="T">The element or value type.</typeparam>
    internal static class PrimitiveCache<T> where T : notnull
    {

        /// <summary>
        /// <c>set-of</c>: the singleton set.
        /// </summary>
        public static readonly Primitive<T, ValueSet<T>> Singleton = new("set-of", static v => ValueSet.Of(v), false);

        /// <summary>
        /// <c>set-of</c> of any number of elements.
        /// </summary>
        public static readonly VariadicPrimitive<T, ValueSet<T>> SetOf = new("set-of", static xs => ValueSet.From(xs));

        /// <summary>
        /// <c>set-union</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Union = new("set-union", static (a, b) => a.Union(b), false);

        /// <summary>
        /// <c>set-intersect</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Intersect = new("set-intersect", static (a, b) => a.Intersect(b), false);

        /// <summary>
        /// <c>set-diff</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Except = new("set-diff", static (a, b) => a.Except(b), false);

        /// <summary>
        /// <c>set-insert</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, T, ValueSet<T>> Insert = new("set-insert", static (s, v) => s.Add(v), false);

        /// <summary>
        /// <c>set-remove</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, T, ValueSet<T>> Remove = new("set-remove", static (s, v) => s.Remove(v), false);

        /// <summary>
        /// <c>set-contains</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, T, bool> Contains = new("set-contains", static (s, v) => s.Contains(v), false)
        {
            FilterName = "set-contains",
        };

        /// <summary>
        /// <c>set-not-contains</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, T, bool> NotContains = new("set-not-contains", static (s, v) => s.Contains(v) == false, false)
        {
            FilterName = "set-not-contains",
        };

        /// <summary>
        /// <c>set-subset</c>: whether the first set is a subset of the second.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, ValueSet<T>, bool> IsSubsetOf = new("set-subset", static (a, b) => a.IsSubsetOf(b), false);

        /// <summary>
        /// <c>set-is-empty</c>: whether the set is empty, an addition (egglog's <c>set-empty</c> is the empty set).
        /// </summary>
        public static readonly Primitive<ValueSet<T>, bool> IsEmpty = new("set-is-empty", static s => s.IsEmpty, false);

        /// <summary>
        /// <c>set-length</c>.
        /// </summary>
        public static readonly Primitive<ValueSet<T>, long> Count = new("set-length", static s => s.Count, false);

        /// <summary>
        /// <c>ordering-min</c>: the value whose cell is the smaller, as egglog's compares representations.
        /// </summary>
        public static readonly Primitive<T, T, T> OrderingMin = new("ordering-min", static (_, _) => throw CellsOnly("ordering-min"),
            static (a, b) => a < b ? a : b);

        /// <summary>
        /// <c>ordering-max</c>: the value whose cell is the larger, as egglog's compares representations.
        /// </summary>
        public static readonly Primitive<T, T, T> OrderingMax = new("ordering-max", static (_, _) => throw CellsOnly("ordering-max"),
            static (a, b) => a > b ? a : b);

        /// <summary>
        /// <c>bool-!=</c>.
        /// </summary>
        public static readonly Primitive<T, T, bool> NotEqual = new("bool-!=", static (a, b) => EqualityComparer<T>.Default.Equals(a, b) == false, false)
        {
            FilterName = "!=",
        };

        /// <summary>
        /// Gets what an ordering primitive's <c>Function</c> throws: it orders cells, which only a graph has.
        /// </summary>
        static InvalidOperationException CellsOnly(string name) =>
            new($"'{name}' orders values by their cells in a graph, as egglog orders them by representation; it runs only in a rule.");

    }

}
