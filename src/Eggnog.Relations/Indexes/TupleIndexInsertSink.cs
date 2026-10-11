using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="IRowSink"/> that adds each scanned key to a <see cref="TupleIndex"/>: the <c>merge_rows</c> half of
    /// egglog's refresh and the body of <c>group_by_key</c>, without the intermediate buffer. Fed by
    /// <see cref="Table.ScanProject{TSink}"/>, whose projection hands it the key's cells contiguously (a one-cell key as a
    /// slice of the row, with no copy).
    /// </summary>
    internal readonly struct TupleIndexInsertSink : IRowSink
    {

        readonly TupleIndex index;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="index">The index to add to.</param>
        public TupleIndexInsertSink(TupleIndex index)
        {
            this.index = index;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row) => index.AddRow(row, rowId);

    }

}
