using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A merge that, on a conflict, writes a row <c>[current, incoming, timestamp]</c> into a log table stamped with
    /// the execution state's captured timestamp, as a bridge <c>Function</c> merge stamps the rows its
    /// <c>lookup_or_insert</c> mints (every write of a run or pass, a merge function's included, carries the epoch the
    /// run captured), and keeps the incoming row.
    /// </summary>
    sealed class StampingMerge : MergeFunction
    {

        readonly TableId log;
        readonly int valueColumn;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="log">The table written; <c>[a, b, ts]</c> sorted by its timestamp.</param>
        /// <param name="valueColumn">The column compared and logged.</param>
        public StampingMerge(TableId log, int valueColumn)
            : base(false)
        {
            this.log = log;
            this.valueColumn = valueColumn;
        }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            if (current[valueColumn] == incoming[valueColumn])
                return false;

            state.StageInsert(log, [current[valueColumn], incoming[valueColumn], state.Timestamp]);
            incoming.CopyTo(output);
            return true;
        }

    }

}
