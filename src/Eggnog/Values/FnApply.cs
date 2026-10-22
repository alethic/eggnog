using System;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>unstable-app</c>: a function value applied to its inputs where the call site's context allows its
    /// function, as <c>FunctionContainer::apply</c> dispatches (src/sort/fn.rs:575-633). A constructor gets or makes the
    /// term only where writes are (write, full); a function reads its row only where reads are (read, full),
    /// none when there is no row; a primitive runs where its own access is allowed (decisions row 12), in the call site's
    /// context. Anywhere else the application fails, as egglog's mismatch panics, even where the call is a filter or a
    /// let that would drop a match: a constructor that cannot make its term would miss silently.
    /// </summary>
    internal static class FnApply
    {

        /// <summary>
        /// Applies a function value over cells: in a rule, a merge or a host write.
        /// </summary>
        /// <param name="arguments">The function value's cell, then the inputs' cells.</param>
        /// <param name="parameters">The function sort's column type, then the inputs'.</param>
        /// <param name="result">The output's column type.</param>
        /// <param name="tables">The call site's tables.</param>
        /// <param name="cell">The result's cell, when there is one.</param>
        /// <returns>Whether there is a result.</returns>
        /// <exception cref="InvalidOperationException">The function is not valid in the call site's context.</exception>
        public static bool TryInvoke(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, PrimitiveTables tables, out uint cell)
        {
            var (target, partial) = ((IFnColumnType)parameters[0]).DecodeCells(arguments[0]);
            var cells = new uint[partial.Length + arguments.Length - 1];
            partial.CopyTo(cells, 0);
            arguments[1..].CopyTo(cells.AsSpan(partial.Length));
            var context = tables.Context;
            switch (target.Kind)
            {
                case FnTargetKind.Constructor:
                    if (CanWrite(context) == false)
                        throw target.NotValidIn(context);

                    cell = tables.MakeCell((ConstructorHandle)target.Table!, cells);
                    return true;
                case FnTargetKind.Function:
                    if (CanRead(context) == false)
                        throw target.NotValidIn(context);

                    return tables.TryLookupCell((FunctionHandle)target.Table!, cells, out cell);
                default:
                {
                    var primitive = target.Primitive!;
                    if (Primitive.Allows(context, primitive.Access) == false)
                        throw target.NotValidIn(context);

                    if (primitive.IsWrite)
                        return primitive.TryInvokeWrite(cells, target.Parameters, result, tables.UnionCells, out cell);

                    if (primitive.UsesTables == false)
                        return primitive.TryInvokeCore(cells, target.Parameters, result, tables.Scope, out cell);

                    var gate = tables.Gate;
                    tables.Gate = primitive;
                    try
                    {
                        return primitive.TryInvokeTables(cells, target.Parameters, result, tables, out cell);
                    }
                    finally
                    {
                        tables.Gate = gate;
                    }
                }
            }
        }

        /// <summary>
        /// Applies a function value over values: in a host read, which reads and writes nothing it does not have to.
        /// </summary>
        /// <param name="arguments">The function value, then the inputs.</param>
        /// <param name="parameters">The function sort's column type, then the inputs'.</param>
        /// <param name="tables">The read's tables.</param>
        /// <param name="value">The result, when there is one.</param>
        /// <returns>Whether there is a result.</returns>
        /// <exception cref="ArgumentException">The function value is a default one.</exception>
        /// <exception cref="InvalidOperationException">The function is not valid in the call site's context.</exception>
        public static bool TryInvokeValue(object[] arguments, ColumnType[] parameters, PrimitiveTables tables, out object? value)
        {
            var content = ((IFnValue)arguments[0]).Content
                ?? throw new ArgumentException($"A default {parameters[0].Name} applies nothing.", nameof(arguments));
            var target = ((IFnColumnType)parameters[0]).Resolve(content.Target);
            var partial = content.ArgumentArray;
            var all = new object[partial.Length + arguments.Length - 1];
            partial.CopyTo(all, 0);
            Array.Copy(arguments, 1, all, partial.Length, arguments.Length - 1);
            var context = tables.Context;
            switch (target.Kind)
            {
                case FnTargetKind.Constructor:
                    // A read makes no term; no read context can.
                    throw target.NotValidIn(context);
                case FnTargetKind.Function:
                    if (CanRead(context) == false)
                        throw target.NotValidIn(context);

                    return tables.TryLookupObject((FunctionHandle)target.Table!, all, out value);
                default:
                {
                    var primitive = target.Primitive!;
                    if (Primitive.Allows(context, primitive.Access) == false || primitive.IsWrite)
                        throw target.NotValidIn(context);

                    if (primitive.UsesTables == false)
                        return primitive.TryInvokeValue(all, target.Parameters, out value);

                    var gate = tables.Gate;
                    tables.Gate = primitive;
                    try
                    {
                        return primitive.TryInvokeValueTables(all, target.Parameters, tables, out value);
                    }
                    finally
                    {
                        tables.Gate = gate;
                    }
                }
            }
        }

        /// <summary>
        /// Gets whether a context writes: egglog's <c>can_mint</c>.
        /// </summary>
        static bool CanWrite(PrimitiveAccess context) => context is PrimitiveAccess.Write or PrimitiveAccess.Full;

        /// <summary>
        /// Gets whether a context reads: egglog's <c>can_read</c>.
        /// </summary>
        static bool CanRead(PrimitiveAccess context) => context is PrimitiveAccess.Read or PrimitiveAccess.Full;

    }

}
