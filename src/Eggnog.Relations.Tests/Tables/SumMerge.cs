using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A merge that adds the incoming value to the current one: associative and commutative but not idempotent, so a
    /// table that drops the merge output (egglog's parallel insert, core-relations/src/table/mod.rs:902-907) gives a
    /// visibly wrong sum. A zero increment changes nothing.
    /// </summary>
    sealed class SumMerge : MergeFunction
    {

        readonly int column;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="column">The column to add up.</param>
        public SumMerge(int column)
            : base(false)
        {
            this.column = column;
        }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            if (incoming[column] == 0)
                return false;

            incoming.CopyTo(output);
            output[column] = unchecked(current[column] + incoming[column]);
            return true;
        }

    }

}
