using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the larger value of a column, comparing raw cells as unsigned integers: the row changes only when the
    /// incoming value is larger. A lattice merge.
    /// </summary>
    /// <remarks>
    /// The comparison is on the stored representation; see <see cref="MinMerge"/>. The output is the incoming row,
    /// keeping its timestamp.
    /// </remarks>
    internal sealed class MaxMerge : MergeFunction
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="valueColumn">The column whose value is merged.</param>
        public MaxMerge(ColumnId valueColumn)
            : base(true)
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
            if (incoming[ValueColumn] <= current[ValueColumn])
                return false;

            incoming.CopyTo(output);
            return true;
        }

    }

}
