using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Replaces the row with the incoming one when its value column differs: egglog's <c>:merge new</c>
    /// (<c>ResolvedMergeFn::New</c> inside the bridge's merge callback, egglog-bridge/src/lib.rs:1067-1101,1185). The
    /// last row inserted for a key wins. Not a lattice merge: the winner depends on arrival order.
    /// </summary>
    /// <remarks>
    /// Only <see cref="ValueColumn"/> decides whether the row changes, so re-deriving the same value at a later
    /// timestamp is not a change. The output is the incoming row, keeping its timestamp.
    /// </remarks>
    internal sealed class KeepIncomingMerge : MergeFunction
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="valueColumn">The column whose value is merged.</param>
        public KeepIncomingMerge(ColumnId valueColumn)
            : base(false)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(valueColumn.Rep, nameof(valueColumn));
            ValueColumn = valueColumn.Rep;
        }

        /// <summary>
        /// Gets the column whose value is merged.
        /// </summary>
        public int ValueColumn { get; }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            if (incoming[ValueColumn] == current[ValueColumn])
                return false;

            incoming.CopyTo(output);
            return true;
        }

    }

}
