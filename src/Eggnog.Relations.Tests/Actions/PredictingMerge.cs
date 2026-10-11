using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A merge function that predicts a row of a table while it merges, for tests: it keeps the current row and reports
    /// what the prediction threw.
    /// </summary>
    sealed class PredictingMerge : MergeFunction
    {

        readonly TableId table;
        readonly CounterId counter;
        readonly Action<Exception> caught;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table to predict into.</param>
        /// <param name="counter">The counter fresh values come from.</param>
        /// <param name="caught">Receives the exception the prediction threw.</param>
        public PredictingMerge(TableId table, CounterId counter, Action<Exception> caught)
            : base(false)
        {
            this.table = table;
            this.counter = counter;
            this.caught = caught;
        }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            try
            {
                state.PredictColumn(table, [99], [counter], new ColumnId(1));
            }
            catch (Exception e)
            {
                caught(e);
            }

            return false;
        }

    }

}
