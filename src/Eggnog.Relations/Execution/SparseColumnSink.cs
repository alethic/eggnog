using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Collects the (value, row) pairs of a column scan of at most <see cref="SparseColumnIndex.Capacity"/> rows,
    /// packed as <c>value &lt;&lt; 32 | row</c> so that sorting the packed words sorts by (value, row). The closure of
    /// egglog's <c>SparseColumnIndex::new</c> (core-relations/src/free_join/execute.rs:90-97).
    /// </summary>
    internal struct SparseColumnSink : IRowSink
    {

        readonly ulong[] pairs;
        int count;

        /// <summary>
        /// Initializes a sink writing into <paramref name="pairs"/>.
        /// </summary>
        /// <param name="pairs">Room for every pair.</param>
        public SparseColumnSink(ulong[] pairs)
        {
            this.pairs = pairs;
            count = 0;
        }

        /// <summary>
        /// Gets the number of pairs collected.
        /// </summary>
        public readonly int Count => count;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            pairs[count++] = (ulong)row[0] << 32 | (uint)rowId;
        }

    }

}
