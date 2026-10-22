using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>unstable-multiset-map</c> (src/sort/multiset.rs:356-409): a function value applied to each distinct
    /// element of a multiset, in cell order, where the call site's context allows its function (<see cref="FnApply"/>);
    /// each result keeps its element's count, results that are one value add their counts, and an element it gives no
    /// result for is dropped. Its arguments are the function value and the multiset. The result is a multiset of its
    /// own sort, rebuilt when that sort's elements are classes, where egglog's copies the input's rebuild flag
    /// (<c>..multiset</c>, multiset.rs:403-406), so a map from values to classes is unequal to the same multiset made
    /// otherwise, and stale after a union: the egglog engine map's defect 27.
    /// </summary>
    /// <typeparam name="T">The input multiset's element type.</typeparam>
    /// <typeparam name="TResult">The output multiset's element type.</typeparam>
    internal sealed class FnMultiSetMapBody<T, TResult> : FnPrimitiveBody
        where T : notnull
        where TResult : notnull
    {

        FnMultiSetMapBody()
        {
        }

        /// <summary>
        /// Gets the body.
        /// </summary>
        public static FnMultiSetMapBody<T, TResult> Instance { get; } = new();

        /// <inheritdoc />
        public override bool IsPartial => false;

        /// <inheritdoc />
        public override bool TryInvoke(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, PrimitiveTables tables, out uint cell)
        {
            var output = (ColumnType<TResult>)((IFnColumnType)parameters[0]).Output;
            var (elements, counts) = ((MultiSetColumnType<T>)parameters[1]).ElementCounts(arguments[1]);
            var mapped = new List<KeyValuePair<TResult, long>>(elements.Length);
            Span<uint> call = [arguments[0], 0];
            for (var i = 0; i < elements.Length; i++)
            {
                call[1] = elements[i];
                if (FnApply.TryInvoke(call, parameters, output, tables, out var applied))
                    mapped.Add(new(output.Decode(applied, tables.Scope), counts[i]));
            }

            cell = tables.EncodeResult((ColumnType<MultiSet<TResult>>)result, MultiSet<TResult>.AddCounts(mapped));
            return true;
        }

        /// <inheritdoc />
        public override bool TryInvokeValue(object[] arguments, ColumnType[] parameters, PrimitiveTables tables, out object? value)
        {
            var mapped = new List<KeyValuePair<TResult, long>>();
            var call = new object[2];
            call[0] = arguments[0];
            foreach (var (element, count) in ((MultiSet<T>)arguments[1]).Counts)
            {
                call[1] = element;
                if (FnApply.TryInvokeValue(call, parameters, tables, out var applied))
                    mapped.Add(new((TResult)applied!, count));
            }

            value = MultiSet<TResult>.AddCounts(mapped);
            return true;
        }

    }

}
