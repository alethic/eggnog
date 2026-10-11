using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="IRowSink"/> that adds each scanned row to a <see cref="ColumnIndex"/> under the values of chosen
    /// columns of the row: the <c>merge_rows</c> half of egglog's <c>refresh_serial</c> and the per-row arm of
    /// <c>build_for_subset</c> (core-relations/src/hash_index/mod.rs:97-112, 526-529), without the intermediate buffer.
    /// </summary>
    /// <remarks>
    /// Reads the columns straight from the scanned row, so the scan needs no projection. A struct, so the table's scan
    /// is specialized for it and the per-row call is direct.
    /// </remarks>
    internal readonly struct ColumnIndexInsertSink : IRowSink
    {

        readonly ColumnIndex index;
        readonly int column;
        readonly int[]? columns;

        /// <summary>
        /// Initializes a sink adding rows under <paramref name="columns"/>, which the caller has checked against the
        /// scanned table's arity.
        /// </summary>
        /// <param name="index">The index to add to.</param>
        /// <param name="columns">The columns; at least one.</param>
        public ColumnIndexInsertSink(ColumnIndex index, int[] columns)
        {
            this.index = index;
            column = columns[0];
            this.columns = columns.Length == 1 ? null : columns;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            if (columns is null)
                index.AddRow(row[column], rowId);
            else
                index.AddRowColumns(row, columns, rowId);
        }

    }

}
