using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>unstable-vec-map</c> (src/sort/vec.rs:231-282): a function value applied to each element of a vector
    /// in order, where the call site's context allows its function (<see cref="FnApply"/>); an element it gives no
    /// result for is dropped. Its arguments are the function value and the vector.
    /// </summary>
    /// <typeparam name="T">The input vector's element type.</typeparam>
    /// <typeparam name="TResult">The output vector's element type.</typeparam>
    internal sealed class FnVecMapBody<T, TResult> : FnPrimitiveBody
        where T : notnull
        where TResult : notnull
    {

        FnVecMapBody()
        {
        }

        /// <summary>
        /// Gets the body.
        /// </summary>
        public static FnVecMapBody<T, TResult> Instance { get; } = new();

        /// <inheritdoc />
        public override bool IsPartial => false;

        /// <inheritdoc />
        public override bool TryInvoke(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, PrimitiveTables tables, out uint cell)
        {
            var output = (ColumnType<TResult>)((IFnColumnType)parameters[0]).Output;
            var elements = new List<(ColumnType Type, uint Cell)>();
            ((IContainerColumnType)parameters[1]).AppendElements(arguments[1], tables.View, elements);
            var mapped = new List<TResult>(elements.Count);
            Span<uint> call = [arguments[0], 0];
            foreach (var (_, element) in elements)
            {
                call[1] = element;
                if (FnApply.TryInvoke(call, parameters, output, tables, out var applied))
                    mapped.Add(output.Decode(applied, tables.Scope));
            }

            cell = tables.EncodeResult((ColumnType<Vec<TResult>>)result, Vec<TResult>.Own(mapped.ToArray()));
            return true;
        }

        /// <inheritdoc />
        public override bool TryInvokeValue(object[] arguments, ColumnType[] parameters, PrimitiveTables tables, out object? value)
        {
            var vector = (Vec<T>)arguments[1];
            var mapped = new List<TResult>(vector.Count);
            var call = new object[2];
            call[0] = arguments[0];
            foreach (var element in vector)
            {
                call[1] = element;
                if (FnApply.TryInvokeValue(call, parameters, tables, out var applied))
                    mapped.Add((TResult)applied!);
            }

            value = Vec<TResult>.Own(mapped.ToArray());
            return true;
        }

    }

}
