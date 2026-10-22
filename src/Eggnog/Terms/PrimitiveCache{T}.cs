using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The generic primitives of <see cref="Primitives"/> for one type, made once per <typeparamref name="T"/>: the set
    /// primitives over its sets (src/sort/set.rs:112-132), and <c>bool-!=</c> over its values. <c>set-get</c> reads the
    /// elements' cells, as egglog's does; in a run or a write, the <c>contains</c> primitives look the element's cell up
    /// in the set's container.
    /// </summary>
    /// <typeparam name="T">The element or value type.</typeparam>
    internal static class PrimitiveCache<T> where T : notnull
    {

        /// <summary>
        /// <c>set-of</c>: the singleton set.
        /// </summary>
        public static readonly Primitive<T, Set<T>> Singleton = new("set-of", static v => Set.Of(v), false);

        /// <summary>
        /// <c>set-of</c> of any number of elements.
        /// </summary>
        public static readonly VariadicPrimitive<T, Set<T>> SetOf = new("set-of", static xs => Set.From(xs));

        /// <summary>
        /// <c>set-union</c>.
        /// </summary>
        public static readonly Primitive<Set<T>, Set<T>, Set<T>> Union = new("set-union", static (a, b) => a.Union(b), false);

        /// <summary>
        /// <c>set-intersect</c>.
        /// </summary>
        public static readonly Primitive<Set<T>, Set<T>, Set<T>> Intersect = new("set-intersect", static (a, b) => a.Intersect(b), false);

        /// <summary>
        /// <c>set-diff</c>.
        /// </summary>
        public static readonly Primitive<Set<T>, Set<T>, Set<T>> Except = new("set-diff", static (a, b) => a.Except(b), false);

        /// <summary>
        /// <c>set-insert</c>.
        /// </summary>
        public static readonly Primitive<Set<T>, T, Set<T>> Insert = new("set-insert", static (s, v) => s.Add(v), false);

        /// <summary>
        /// <c>set-remove</c>.
        /// </summary>
        public static readonly Primitive<Set<T>, T, Set<T>> Remove = new("set-remove", static (s, v) => s.Remove(v), false);

        /// <summary>
        /// <c>set-contains</c>.
        /// </summary>
        public static readonly Primitive<Set<T>, T, bool> Contains = new(
            "bool-set-contains",
            static (s, v) => s.Contains(v),
            null,
            static (set, _, result, setCell, itemCell) => (true, Encode(result, Holds(set, setCell, itemCell))))
        {
            FilterName = "set-contains",
        };

        /// <summary>
        /// <c>set-not-contains</c>.
        /// </summary>
        public static readonly Primitive<Set<T>, T, bool> NotContains = new(
            "bool-set-not-contains",
            static (s, v) => s.Contains(v) == false,
            null,
            static (set, _, result, setCell, itemCell) => (true, Encode(result, Holds(set, setCell, itemCell) == false)))
        {
            FilterName = "set-not-contains",
        };

        /// <summary>
        /// <c>set-subset</c>: whether the first set is a subset of the second.
        /// </summary>
        public static readonly Primitive<Set<T>, Set<T>, bool> IsSubsetOf = new("set-subset", static (a, b) => a.IsSubsetOf(b), false);

        /// <summary>
        /// <c>set-is-empty</c>: whether the set is empty, an addition (egglog's <c>set-empty</c> is the empty set).
        /// </summary>
        public static readonly Primitive<Set<T>, bool> IsEmpty = new("set-is-empty", static s => s.IsEmpty, false);

        /// <summary>
        /// <c>set-length</c>.
        /// </summary>
        public static readonly Primitive<Set<T>, long> Count = new("set-length", static s => s.Count, false);

        /// <summary>
        /// <c>set-get</c>: the element at an index in cell order; no result for an index out of range.
        /// </summary>
        public static readonly Primitive<Set<T>, long, T> Get = new(
            "set-get",
            static (set, index, _, setCell, indexCell) =>
                ((SetColumnType<T>)set).TryGetAt(setCell, ((ColumnType<long>)index).Decode(indexCell, null), out var item) ? (true, item) : (false, 0u),
            static (set, value, index) => ((SetColumnType<T>)set).TryGetAtValue(value, index, out var item) ? (true, item) : (false, default!));

        static bool Holds(ColumnType set, uint setCell, uint itemCell) => ((SetColumnType<T>)set).ContainsCell(setCell, itemCell);

        static uint Encode(ColumnType result, bool value) => ((ColumnType<bool>)result).Encode(value);

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
        public static readonly Primitive<T, T, bool> NotEqual = new("bool-!=", static (a, b) => EqualityComparer<T>.Default.Equals(a, b) == false, static (uint a, uint b) => a != b)
        {
            FilterName = "!=",
        };

        /// <summary>
        /// The equality egglog's <c>value-eq</c> is the fallible form of: of cells, as egglog compares representations.
        /// </summary>
        static readonly Primitive<T, T, bool> Same = new("bool-value-eq", static (a, b) => EqualityComparer<T>.Default.Equals(a, b), static (uint a, uint b) => a == b)
        {
            FilterName = "value-eq",
        };

        /// <summary>
        /// <c>value-eq</c>: a result, <c>()</c>, where the two values are one, none where they differ (src/lib.rs:453).
        /// </summary>
        public static readonly Primitive ValueEq = Same.FallibleForm;

        /// <summary>
        /// Gets what an ordering primitive's <c>Function</c> throws: it orders cells, which only a graph has.
        /// </summary>
        static InvalidOperationException CellsOnly(string name) =>
            new($"'{name}' orders values by their cells in a graph, as egglog orders them by representation; it runs only in a rule.");

    }

}
