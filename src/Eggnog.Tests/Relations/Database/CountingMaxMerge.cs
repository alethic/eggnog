using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Keeps the larger value of a column and advances a database counter on every call through the execution state's
    /// view: a merge that needs the database, run from cloned states by a table's parallel insert.
    /// </summary>
    sealed class CountingMaxMerge : MergeFunction
    {

        readonly CounterId counter;
        readonly int valueColumn;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="counter">The counter advanced per call.</param>
        /// <param name="valueColumn">The merged column.</param>
        public CountingMaxMerge(CounterId counter, int valueColumn)
            : base(true)
        {
            this.counter = counter;
            this.valueColumn = valueColumn;
        }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            state.IncrementCounter(counter);
            if (incoming[valueColumn] <= current[valueColumn])
                return false;

            incoming.CopyTo(output);
            return true;
        }

    }

}
