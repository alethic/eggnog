using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>unstable-multiset-fill-index</c> and <c>unstable-multiset-clear-index</c> (src/sort/multiset.rs:411-536):
    /// the rows of a function value's function at a multiset and each of its elements, which hold the element's count.
    /// They use the function value's function only, as egglog's use its table action, keyed by the multiset and an
    /// element, so a value with partially applied arguments is refused, where egglog's fails on the keys. A primitive has
    /// no rows: they have no result for one, as egglog 3.0.0's (src/sort/multiset.rs:518-520, 583-585), where the pin
    /// panics.
    /// </summary>
    /// <typeparam name="T">The multiset's element type.</typeparam>
    internal static class FnMultiSetIndex<T>
        where T : notnull
    {

        /// <summary>
        /// <c>unstable-multiset-fill-index</c>: sets the function's row at the multiset and each distinct element, in cell
        /// order, to the element's count, and stops at the first such row there is, as egglog's does, since it writes all
        /// of a multiset's rows in one pass. A full primitive: it reads the rows it skips, so it runs only where reads and
        /// writes are, a naive rule's head or a host write.
        /// </summary>
        public static bool Fill(ITables tables, ReadOnlySpan<object> arguments, out ValueTuple result)
        {
            result = default;
            var multiset = arguments[0];
            if (Index(arguments[1], "unstable-multiset-fill-index") is not { } index)
                return false;

            foreach (var (element, count) in Ordered(tables, (MultiSet<T>)multiset))
            {
                if (tables.TryLookup<long>(index, [multiset, element], out _))
                    break;

                tables.Set(index, [multiset, element], count);
            }

            return true;
        }

        /// <summary>
        /// <c>unstable-multiset-clear-index</c>: deletes the function's row at the multiset and each distinct element. A
        /// write primitive: it runs where writes are, a rule's head or a host write.
        /// </summary>
        public static bool Clear(ITableWriter tables, ReadOnlySpan<object> arguments, out ValueTuple result)
        {
            result = default;
            var multiset = arguments[0];
            if (Index(arguments[1], "unstable-multiset-clear-index") is not { } index)
                return false;

            foreach (var (element, _) in ((MultiSet<T>)multiset).Counts)
                tables.Remove(index, [multiset, element]);

            return true;
        }

        /// <summary>
        /// Gets the function a function value applies, as egglog takes its table action; <c>null</c> for a primitive,
        /// which has no rows.
        /// </summary>
        /// <exception cref="ArgumentException">The value is a default one.</exception>
        /// <exception cref="InvalidOperationException">The value has partially applied arguments, which its rows, keyed by
        /// the multiset and an element, have no place for.</exception>
        static FunctionHandle? Index(object value, string name)
        {
            var content = ((IFnValue)value).Content ?? throw new ArgumentException($"A default function value names no function for {name}.");
            if (content.ArgumentArray.Length > 0)
                throw new InvalidOperationException(
                    $"{name} keys its function's rows by the multiset and an element, so its function value takes no partially applied arguments; '{content.Name}' has {content.ArgumentArray.Length}.");

            return content.Target as FunctionHandle;
        }

        /// <summary>
        /// Gets a multiset's distinct elements and counts in cell order, egglog's, where the scope knows it
        /// (<see cref="MultiSetColumnType{T}.TryOrderByCell"/>); else in element order.
        /// </summary>
        static IEnumerable<KeyValuePair<T, long>> Ordered(ITables tables, MultiSet<T> multiset) =>
            tables is PrimitiveTables { Scope: { } scope }
                && scope.Graph.Values.TypeOf<MultiSet<T>>() is MultiSetColumnType<T> type
                && type.TryOrderByCell(multiset, out var ordered)
                ? ordered
                : multiset.Counts;

    }

}
