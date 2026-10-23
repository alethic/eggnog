using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>unstable-multiset-reduce</c> (src/sort/multiset.rs:721-783): a binary function value folded over a
    /// multiset's elements, each as many times as its count, in cell order, from the first element, or the initial value
    /// of an empty multiset; each application where the call site's context allows its function
    /// (<see cref="FnApply"/>), and no result once one has none. Its arguments are the function value, the initial value
    /// and the multiset.
    /// </summary>
    /// <typeparam name="T">The multiset's element type.</typeparam>
    internal sealed class FnMultiSetReduceBody<T> : FnPrimitiveBody
        where T : notnull
    {

        FnMultiSetReduceBody()
        {
        }

        /// <summary>
        /// Gets the body.
        /// </summary>
        public static FnMultiSetReduceBody<T> Instance { get; } = new();

        /// <inheritdoc />
        public override bool IsPartial => true;

        /// <inheritdoc />
        public override bool TryInvoke(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, PrimitiveTables tables, out uint cell)
        {
            var (elements, counts) = ((MultiSetColumnType<T>)parameters[2]).ElementCounts(arguments[2], tables.View);
            cell = arguments[1];
            var first = true;
            Span<uint> call = [arguments[0], 0, 0];
            for (var i = 0; i < elements.Length; i++)
            {
                for (var n = 0L; n < counts[i]; n++)
                {
                    if (first)
                    {
                        cell = elements[i];
                        first = false;
                        continue;
                    }

                    call[1] = cell;
                    call[2] = elements[i];
                    if (FnApply.TryInvoke(call, parameters, result, tables, out cell) == false)
                        return false;
                }
            }

            return true;
        }

        /// <inheritdoc />
        /// <remarks>The elements fold in the order egglog's <c>check</c> would hold them in once it interned them: those
        /// writes interned in cell order, then one no write interned; two or more no write interned have no order, so no
        /// result (<see cref="MultiSetColumnType{T}.TryOrderByCell"/>, decisions row 22).</remarks>
        public override bool TryInvokeValue(object[] arguments, ColumnType[] parameters, PrimitiveTables tables, out object? value)
        {
            if (((MultiSetColumnType<T>)parameters[2]).TryOrderByCell((MultiSet<T>)arguments[2], tables.View, out var entries) == false)
            {
                value = null;
                return false;
            }

            value = arguments[1];
            var first = true;
            var call = new object[3];
            call[0] = arguments[0];
            foreach (var (item, count) in entries)
            {
                for (var n = 0L; n < count; n++)
                {
                    if (first)
                    {
                        value = item;
                        first = false;
                        continue;
                    }

                    call[1] = value!;
                    call[2] = item;
                    if (FnApply.TryInvokeValue(call, parameters, tables, out value) == false)
                        return false;
                }
            }

            return true;
        }

    }

}
