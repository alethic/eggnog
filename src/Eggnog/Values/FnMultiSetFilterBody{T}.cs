using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>unstable-multiset-filter</c> and <c>unstable-multiset-filter-not</c> (src/sort/multiset.rs:605-662): a
    /// function value into <c>Unit</c> applied to each distinct element of a multiset, in cell order, where the call
    /// site's context allows its function (<see cref="FnApply"/>); the elements it has a result for are kept, with their
    /// counts, by the filter, and the others by the filter-not. Its arguments are the function value and the multiset.
    /// </summary>
    /// <typeparam name="T">The multiset's element type.</typeparam>
    internal sealed class FnMultiSetFilterBody<T> : FnPrimitiveBody
        where T : notnull
    {

        readonly bool keepDefined;

        FnMultiSetFilterBody(bool keepDefined)
        {
            this.keepDefined = keepDefined;
        }

        /// <summary>
        /// Gets the filter, which keeps the elements the function has a result for.
        /// </summary>
        public static FnMultiSetFilterBody<T> Filter { get; } = new(true);

        /// <summary>
        /// Gets the filter-not, which keeps the elements the function has no result for.
        /// </summary>
        public static FnMultiSetFilterBody<T> FilterNot { get; } = new(false);

        /// <inheritdoc />
        public override bool IsPartial => false;

        /// <inheritdoc />
        public override bool TryInvoke(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, PrimitiveTables tables, out uint cell)
        {
            var output = ((IFnColumnType)parameters[0]).Output;
            var multisets = (MultiSetColumnType<T>)parameters[1];
            var element = (ColumnType<T>)multisets.ElementTypes[0];
            var (elements, counts) = multisets.ElementCounts(arguments[1]);
            var kept = new List<KeyValuePair<T, long>>(elements.Length);
            Span<uint> call = [arguments[0], 0];
            for (var i = 0; i < elements.Length; i++)
            {
                call[1] = elements[i];
                if (FnApply.TryInvoke(call, parameters, output, tables, out _) == keepDefined)
                    kept.Add(new(element.Decode(elements[i], tables.Scope), counts[i]));
            }

            cell = tables.EncodeResult((ColumnType<MultiSet<T>>)result, MultiSet<T>.FromCounts(kept));
            return true;
        }

        /// <inheritdoc />
        public override bool TryInvokeValue(object[] arguments, ColumnType[] parameters, PrimitiveTables tables, out object? value)
        {
            var kept = new List<KeyValuePair<T, long>>();
            var call = new object[2];
            call[0] = arguments[0];
            foreach (var (element, count) in ((MultiSet<T>)arguments[1]).Counts)
            {
                call[1] = element;
                if (FnApply.TryInvokeValue(call, parameters, tables, out _) == keepDefined)
                    kept.Add(new(element, count));
            }

            value = MultiSet<T>.FromCounts(kept);
            return true;
        }

    }

}
