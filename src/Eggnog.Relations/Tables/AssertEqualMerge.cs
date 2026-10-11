using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Requires an incoming value to equal the one the table holds: egglog's default merge for functions without one
    /// (<c>ResolvedMergeFn::AssertEq</c>, egglog-bridge/src/lib.rs:1186-1192). A conflict is reported through
    /// <see cref="ExecutionState.ReportFailure"/>, which also requests early stopping, where egglog calls a panic
    /// function; the table keeps its row. A lattice merge (equal values merge to themselves; anything else fails
    /// whatever the order).
    /// </summary>
    internal sealed class AssertEqualMerge : MergeFunction
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="valueColumn">The column whose values must agree.</param>
        /// <param name="name">The table's name, for the failure message.</param>
        public AssertEqualMerge(ColumnId valueColumn, string? name = null)
            : base(true)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(valueColumn.Rep, nameof(valueColumn));
            ValueColumn = valueColumn.Rep;
            Name = name;
        }

        /// <summary>
        /// Gets the column whose values must agree.
        /// </summary>
        public int ValueColumn { get; }

        /// <summary>
        /// Gets the table's name used in failure messages, if any.
        /// </summary>
        public string? Name { get; }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            var cur = current[ValueColumn];
            var inc = incoming[ValueColumn];
            if (cur != inc)
                state.ReportFailure($"Illegal merge attempted for function {Name ?? "<unnamed>"}: {cur} conflicts with {inc}.");

            return false;
        }

    }

}
