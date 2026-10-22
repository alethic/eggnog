using System;

namespace Eggnog
{

    /// <summary>
    /// What a primitive that applies function values does: egglog's <c>unstable-app</c> (<see cref="FnApplyBody"/>) and
    /// its higher-order container primitives, pure themselves, each application dispatched on the call site's context
    /// as egglog's <c>apply_function</c> dispatches it (src/exec_state.rs:207-221, <see cref="FnApply"/>).
    /// </summary>
    internal abstract class FnPrimitiveBody
    {

        /// <summary>
        /// Gets whether a call may have no result.
        /// </summary>
        public abstract bool IsPartial { get; }

        /// <summary>
        /// Runs the primitive over cells: in a rule, a merge or a host write.
        /// </summary>
        /// <param name="arguments">The argument cells.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="result">The result's column type.</param>
        /// <param name="tables">The call site's tables.</param>
        /// <param name="cell">The result's cell, when there is one.</param>
        /// <returns>Whether there is a result.</returns>
        /// <exception cref="InvalidOperationException">A function is not valid in the call site's context.</exception>
        public abstract bool TryInvoke(ReadOnlySpan<uint> arguments, ColumnType[] parameters, ColumnType result, PrimitiveTables tables, out uint cell);

        /// <summary>
        /// Runs the primitive over values: in a host read, which reads and writes nothing it does not have to.
        /// </summary>
        /// <param name="arguments">The arguments.</param>
        /// <param name="parameters">The parameters' column types.</param>
        /// <param name="tables">The read's tables.</param>
        /// <param name="value">The result, when there is one.</param>
        /// <returns>Whether there is a result.</returns>
        /// <exception cref="InvalidOperationException">A function is not valid in the call site's context.</exception>
        public abstract bool TryInvokeValue(object[] arguments, ColumnType[] parameters, PrimitiveTables tables, out object? value);

    }

}
