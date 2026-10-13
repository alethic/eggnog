using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A lattice merge that joins bit sets: the output's value is the bitwise or of the current and incoming values, so
    /// unlike <see cref="MinMerge"/>, <see cref="MaxMerge"/> and <see cref="UnionIdMerge"/> its output is in general
    /// neither input. A layered merge that stored the incoming row instead of the merge output would lose the current
    /// row's bits, which those merges cannot show. An incoming value whose bits are all set already changes nothing.
    /// </summary>
    sealed class OrMerge : MergeFunction
    {

        readonly int column;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="column">The column holding the bit set.</param>
        public OrMerge(int column)
            : base(true)
        {
            this.column = column;
        }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            var joined = current[column] | incoming[column];
            if (joined == current[column])
                return false;

            incoming.CopyTo(output);
            output[column] = joined;
            return true;
        }

    }

}
