using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A lattice merge, by the larger value in column 1, that first reads another table through the merge's execution
    /// state (<see cref="ExecutionState.PredictColumn(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, ColumnId)"/>),
    /// for tests of reads of the stratum being merged.
    /// </summary>
    sealed class StratumReadingMerge : MergeFunction
    {

        readonly TableId other;

        /// <summary>
        /// Initializes a merge that reads <paramref name="other"/>, a one-key table with a value column.
        /// </summary>
        public StratumReadingMerge(TableId other)
            : base(true)
        {
            this.other = other;
        }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            state.PredictColumn(other, incoming[..1], [MergeVal.FromConstant(new Value(0)), MergeVal.FromConstant(new Value(0))], new ColumnId(1));
            if (incoming[1] <= current[1])
                return false;

            incoming.CopyTo(output);
            return true;
        }

    }

}
