using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>unstable-multiset-flat-map</c> (src/sort/multiset.rs:538-603): a function value from an element to a
    /// multiset applied to each distinct element of a multiset, in cell order, where the call site's context allows its
    /// function (<see cref="FnApply"/>), and the results summed, each element of a result counted times its element's
    /// count; an element it gives no result for is kept as it is. Its arguments are the function value and the
    /// multiset.
    /// </summary>
    /// <typeparam name="T">The multiset's element type.</typeparam>
    internal sealed class FnMultiSetFlatMapBody<T> : FnPrimitiveBody
        where T : notnull
    {

        FnMultiSetFlatMapBody()
        {
        }

        /// <summary>
        /// Gets the body.
        /// </summary>
        public static FnMultiSetFlatMapBody<T> Instance { get; } = new();

        /// <inheritdoc />
        public override bool IsPartial => false;

        /// <inheritdoc />
        public override bool TryInvoke(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, PrimitiveTables tables, out uint cell)
        {
            var output = (ColumnType<MultiSet<T>>)((IFnColumnType)parameters[0]).Output;
            var multisets = (MultiSetColumnType<T>)parameters[1];
            var element = (ColumnType<T>)multisets.ElementTypes[0];
            var (elements, counts) = multisets.ElementCounts(arguments[1], tables.View);
            var summed = new List<KeyValuePair<T, long>>(elements.Length);
            Span<uint> call = [arguments[0], 0];
            for (var i = 0; i < elements.Length; i++)
            {
                call[1] = elements[i];
                if (FnApply.TryInvoke(call, parameters, output, tables, out var applied))
                {
                    foreach (var (item, count) in output.Decode(applied, tables.Scope).Counts)
                        summed.Add(new(item, checked(count * counts[i])));
                }
                else
                {
                    summed.Add(new(element.Decode(elements[i], tables.Scope), counts[i]));
                }
            }

            cell = tables.EncodeResult((ColumnType<MultiSet<T>>)result, MultiSet<T>.AddCounts(summed));
            return true;
        }

        /// <inheritdoc />
        public override bool TryInvokeValue(object[] arguments, ColumnType[] parameters, PrimitiveTables tables, out object? value)
        {
            var summed = new List<KeyValuePair<T, long>>();
            var call = new object[2];
            call[0] = arguments[0];
            foreach (var (element, count) in ((MultiSet<T>)arguments[1]).Counts)
            {
                call[1] = element;
                if (FnApply.TryInvokeValue(call, parameters, tables, out var applied))
                {
                    foreach (var (item, inner) in ((MultiSet<T>)applied!).Counts)
                        summed.Add(new(item, checked(inner * count)));
                }
                else
                {
                    summed.Add(new(element, count));
                }
            }

            value = MultiSet<T>.AddCounts(summed);
            return true;
        }

    }

}
