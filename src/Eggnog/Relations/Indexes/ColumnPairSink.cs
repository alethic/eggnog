using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="IRowSink"/> that records, for each scanned row and each distinct value among chosen columns of the
    /// row, the pair (value, row id) packed as <c>value &lt;&lt; 32 | row</c>: the pair collection of egglog's sort-based
    /// <c>ColumnIndex::rebuild_full</c> (core-relations/src/hash_index/mod.rs:342-349), also used by the parallel build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pairs are emitted row by row, all columns of a row together, so they come out in non-decreasing row order: a
    /// stable sort by value then orders them by (value, row) (<see cref="PairRadixSort"/>). egglog instead collects one
    /// column after another, so with several columns a value's rows come out as the first column's rows followed by the
    /// second's, unsorted, and <c>dedup</c> misses non-adjacent duplicates
    /// (core-relations/src/hash_index/mod.rs:341-382). A value repeated in two columns of one row is emitted once,
    /// which is the deduplication egglog intended.
    /// </para>
    /// <para>
    /// The pair array must have room for every pair: the scanned subset's size times the number of columns. A struct
    /// passed by reference; read <see cref="Count"/> after the scan.
    /// </para>
    /// </remarks>
    internal struct ColumnPairSink : IRowSink
    {

        readonly ulong[] pairs;
        readonly int column;
        readonly int[]? columns;
        int count;
#if DEBUG
        int lastRow;
#endif

        /// <summary>
        /// Initializes a sink writing into <paramref name="pairs"/> from position zero.
        /// </summary>
        /// <param name="pairs">Receives the packed pairs.</param>
        /// <param name="columns">The columns, checked against the scanned table's arity; at least one.</param>
        public ColumnPairSink(ulong[] pairs, int[] columns)
        {
            this.pairs = pairs;
            column = columns[0];
            this.columns = columns.Length == 1 ? null : columns;
            count = 0;
#if DEBUG
            lastRow = 0;
#endif
        }

        /// <summary>
        /// Gets the number of pairs written.
        /// </summary>
        public readonly int Count => count;

        /// <summary>
        /// Packs a (value, row) pair so that ordering the packed words orders by value, then row.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="row">The row id.</param>
        /// <returns>The packed pair.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Pack(uint value, int row) => ((ulong)value << 32) | (uint)row;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
#if DEBUG
            Debug.Assert(rowId >= lastRow, "the scan must visit rows in ascending order");
            lastRow = rowId;
#endif
            if (columns is null)
            {
                pairs[count++] = Pack(row[column], rowId);
                return;
            }

            for (var i = 0; i < columns.Length; i++)
            {
                if (ColumnIndex.RepeatsEarlierColumn(row, columns, i) == false)
                    pairs[count++] = Pack(row[columns[i]], rowId);
            }
        }

    }

}
