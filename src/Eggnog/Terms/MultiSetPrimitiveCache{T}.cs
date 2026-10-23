using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The multiset primitives of <see cref="Primitives"/> for one element type, made once per <typeparamref name="T"/>:
    /// egglog's first-order <c>multiset-*</c> primitives over values (src/sort/multiset.rs:128-209). <c>multiset-pick</c>
    /// and <c>multiset-pick-max</c> read the elements' cells, as egglog's do; <c>multiset-pick</c> has no result for an
    /// empty multiset, as egglog 3.0.0's, where the pin panics (decisions row 22). In a run or a write, the
    /// <c>contains</c> primitives and <c>multiset-count</c> look the element's cell up in the multiset's container.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal static class MultiSetPrimitiveCache<T> where T : notnull
    {

        /// <summary>
        /// <c>multiset-of</c>.
        /// </summary>
        public static readonly VariadicPrimitive<T, MultiSet<T>> Of = new("multiset-of", static xs => MultiSet.From(xs));

        /// <summary>
        /// <c>multiset-single</c>: no result for a negative count.
        /// </summary>
        public static readonly Primitive<T, long, MultiSet<T>> Single =
            BuiltInPrimitive.Partial<T, long, MultiSet<T>>("multiset-single", TrySingle);

        /// <summary>
        /// <c>multiset-pick</c>: the element of least cell; no result for an empty multiset.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, T> Pick = new(
            "multiset-pick",
            static (type, cell, view) => ((MultiSetColumnType<T>)type).TryPick(cell, view, out var picked) ? (true, picked) : (false, 0u),
            static (type, value, view) => ((MultiSetColumnType<T>)type).TryPickValue(value, view, out var picked) ? (true, picked) : (false, default!));

        /// <summary>
        /// <c>multiset-insert</c>.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, T, MultiSet<T>> Insert = new("multiset-insert", static (m, x) => m.Insert(x), false);

        /// <summary>
        /// <c>multiset-remove</c>: no result when the element does not occur.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, T, MultiSet<T>> Remove =
            BuiltInPrimitive.Partial<MultiSet<T>, T, MultiSet<T>>("multiset-remove", TryRemove);

        /// <summary>
        /// <c>multiset-remove-swapped</c>: <c>multiset-remove</c> with its arguments swapped.
        /// </summary>
        public static readonly Primitive<T, MultiSet<T>, MultiSet<T>> RemoveSwapped =
            BuiltInPrimitive.Partial<T, MultiSet<T>, MultiSet<T>>("multiset-remove-swapped", TryRemoveSwapped);

        /// <summary>
        /// <c>multiset-subtract</c>: no result when an element of the second occurs fewer times in the first.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, MultiSet<T>, MultiSet<T>> Subtract =
            BuiltInPrimitive.Partial<MultiSet<T>, MultiSet<T>, MultiSet<T>>("multiset-subtract", TrySubtract);

        /// <summary>
        /// <c>multiset-subtract-swapped</c>: the second minus the first.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, MultiSet<T>, MultiSet<T>> SubtractSwapped =
            BuiltInPrimitive.Partial<MultiSet<T>, MultiSet<T>, MultiSet<T>>("multiset-subtract-swapped", TrySubtractSwapped);

        /// <summary>
        /// <c>multiset-length</c>: the count of elements with their repeats.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, long> Length = new("multiset-length", static m => m.Count, false);

        /// <summary>
        /// <c>multiset-contains</c>.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, T, bool> Contains = new(
            "bool-multiset-contains",
            static (m, x) => m.Contains(x),
            null,
            static (multiset, _, result, multisetCell, itemCell, view) => (true, Encode(result, Occurs(multiset, multisetCell, itemCell, view))))
        {
            FilterName = "multiset-contains",
        };

        /// <summary>
        /// <c>multiset-not-contains</c>.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, T, bool> NotContains = new(
            "bool-multiset-not-contains",
            static (m, x) => m.Contains(x) == false,
            null,
            static (multiset, _, result, multisetCell, itemCell, view) => (true, Encode(result, Occurs(multiset, multisetCell, itemCell, view) == false)))
        {
            FilterName = "multiset-not-contains",
        };

        /// <summary>
        /// <c>multiset-contains-swapped</c>.
        /// </summary>
        public static readonly Primitive<T, MultiSet<T>, bool> ContainsSwapped = new(
            "bool-multiset-contains-swapped",
            static (x, m) => m.Contains(x),
            null,
            static (_, multiset, result, itemCell, multisetCell, view) => (true, Encode(result, Occurs(multiset, multisetCell, itemCell, view))))
        {
            FilterName = "multiset-contains-swapped",
        };

        /// <summary>
        /// <c>multiset-not-contains-swapped</c>.
        /// </summary>
        public static readonly Primitive<T, MultiSet<T>, bool> NotContainsSwapped = new(
            "bool-multiset-not-contains-swapped",
            static (x, m) => m.Contains(x) == false,
            null,
            static (_, multiset, result, itemCell, multisetCell, view) => (true, Encode(result, Occurs(multiset, multisetCell, itemCell, view) == false)))
        {
            FilterName = "multiset-not-contains-swapped",
        };

        /// <summary>
        /// <c>multiset-intersection</c>: each element's lesser count.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, MultiSet<T>, MultiSet<T>> Intersection =
            new("multiset-intersection", static (a, b) => a.Intersect(b), false);

        /// <summary>
        /// <c>multiset-sum</c>: each element's counts added.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, MultiSet<T>, MultiSet<T>> Sum =
            new("multiset-sum", static (a, b) => a.Sum(b), false);

        /// <summary>
        /// <c>multiset-reset-counts</c>: every count one.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, MultiSet<T>> ResetCounts =
            new("multiset-reset-counts", static m => m.ResetCounts(), false);

        /// <summary>
        /// <c>multiset-pick-max</c>: an element of greatest count, of those the one of greatest cell; no result for an
        /// empty multiset.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, T> PickMax = new(
            "multiset-pick-max",
            static (type, cell, view) => ((MultiSetColumnType<T>)type).TryPickMax(cell, view, out var picked) ? (true, picked) : (false, 0u),
            static (type, value, view) => ((MultiSetColumnType<T>)type).TryPickMaxValue(value, view, out var picked) ? (true, picked) : (false, default!));

        /// <summary>
        /// <c>multiset-count</c>: an element's count, 0 when it does not occur.
        /// </summary>
        public static readonly Primitive<MultiSet<T>, T, long> Count = new(
            "multiset-count",
            static (m, x) => m.CountOf(x),
            null,
            static (multiset, _, result, multisetCell, itemCell, view) =>
                (true, ((ColumnType<long>)result).Encode(((MultiSetColumnType<T>)multiset).CountOfCell(multisetCell, itemCell, view))));

        /// <summary>
        /// <c>multiset-sum-multisets</c>: the sum of a multiset of multisets, each counted as often as it occurs.
        /// </summary>
        public static readonly Primitive<MultiSet<MultiSet<T>>, MultiSet<T>> SumMultiSets = new("multiset-sum-multisets", static outer =>
        {
            var counts = new Dictionary<T, long>();
            foreach (var (inner, times) in outer.Counts)
                foreach (var (item, count) in inner.Counts)
                    counts[item] = checked(counts.GetValueOrDefault(item) + count * times);

            return MultiSet<T>.FromCounts([.. counts]);
        }, false);

        static bool TrySingle(T item, long count, out MultiSet<T> result)
        {
            if (count < 0)
            {
                result = MultiSet<T>.Empty;
                return false;
            }

            result = count == 0 ? MultiSet<T>.Empty : MultiSet<T>.FromCounts([new KeyValuePair<T, long>(item, count)]);
            return true;
        }

        static bool Occurs(ColumnType multiset, uint multisetCell, uint itemCell, Relations.ContainerView view) =>
            ((MultiSetColumnType<T>)multiset).CountOfCell(multisetCell, itemCell, view) > 0;

        static uint Encode(ColumnType result, bool value) => ((ColumnType<bool>)result).Encode(value);

        static bool TryRemove(MultiSet<T> multiset, T item, out MultiSet<T> result)
        {
            result = multiset.Remove(item);
            return multiset.Contains(item);
        }

        static bool TryRemoveSwapped(T item, MultiSet<T> multiset, out MultiSet<T> result) => TryRemove(multiset, item, out result);

        static bool TrySubtract(MultiSet<T> multiset, MultiSet<T> other, out MultiSet<T> result) =>
            multiset.TrySubtract(other, out result);

        static bool TrySubtractSwapped(MultiSet<T> other, MultiSet<T> multiset, out MultiSet<T> result) =>
            multiset.TrySubtract(other, out result);

    }

}
