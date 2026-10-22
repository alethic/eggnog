using System;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>unstable-app</c>: a function value applied to the inputs (<see cref="FnApply"/>), partial as a lookup
    /// that misses is.
    /// </summary>
    internal sealed class FnApplyBody : FnPrimitiveBody
    {

        FnApplyBody()
        {
        }

        /// <summary>
        /// Gets the body.
        /// </summary>
        public static FnApplyBody Instance { get; } = new();

        /// <inheritdoc />
        public override bool IsPartial => true;

        /// <inheritdoc />
        public override bool TryInvoke(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, PrimitiveTables tables, out uint cell) =>
            FnApply.TryInvoke(arguments, parameters, result, tables, out cell);

        /// <inheritdoc />
        public override bool TryInvokeValue(object[] arguments, ColumnType[] parameters, PrimitiveTables tables, out object? value) =>
            FnApply.TryInvokeValue(arguments, parameters, tables, out value);

    }

}
