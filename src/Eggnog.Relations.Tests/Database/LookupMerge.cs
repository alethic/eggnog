using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A merge that reads another table, as the bridge's <c>Function</c> merge does: on a conflict, the row becomes the
    /// incoming one with its value column replaced by what <see cref="Source"/> holds for the same key (through the
    /// execution state's view, which fails for a table being merged). Keeps the current row when that changes nothing.
    /// </summary>
    sealed class LookupMerge : MergeFunction
    {

        readonly int keyCount;
        readonly int valueColumn;
        readonly int sourceColumn;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="source">The table read.</param>
        /// <param name="keyCount">The number of key columns, shared with the source.</param>
        /// <param name="valueColumn">The column replaced.</param>
        /// <param name="sourceColumn">The source column read.</param>
        public LookupMerge(TableId source, int keyCount, int valueColumn, int sourceColumn)
            : base(false)
        {
            Source = source;
            this.keyCount = keyCount;
            this.valueColumn = valueColumn;
            this.sourceColumn = sourceColumn;
        }

        /// <summary>
        /// Gets the table the merge reads.
        /// </summary>
        public TableId Source { get; }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            var table = state.GetTable(Source);
            var value = table.TryGetRowColumn(incoming[..keyCount], new ColumnId(sourceColumn), out var found) ? found : incoming[valueColumn];
            incoming.CopyTo(output);
            output[valueColumn] = value;
            return output.SequenceEqual(current) == false;
        }

    }

}
