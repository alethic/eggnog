using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="IRowSink"/> that forwards each row's projection onto chosen columns to an inner sink: the
    /// projection closures of egglog's <c>scan_project</c> and <c>for_each_col</c>
    /// (core-relations/src/table_spec.rs:441-488), specialized for one, two, three and more columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One column is forwarded as a one-cell slice of the scanned row, with no copy. Two or three columns are gathered
    /// by unrolled code into a scratch array; more by a loop. The column count is fixed for a scan, so the switch in
    /// <see cref="AddRow"/> is perfectly predicted. The scan is generic over this struct and the struct over the inner
    /// sink, so the whole path is specialized and inlinable, where egglog calls <c>&amp;mut dyn RowSink</c> per row.
    /// </para>
    /// <para>
    /// The scratch array is rented from the shared pool; <see cref="Release"/> returns it. The inner sink must not
    /// keep the spans it receives.
    /// </para>
    /// </remarks>
    /// <typeparam name="TSink">The inner sink type.</typeparam>
    internal struct ProjectingSink<TSink> : IRowSink
        where TSink : struct, IRowSink
    {

        TSink inner;
        readonly int count;
        readonly int c0;
        readonly int c1;
        readonly int c2;
        readonly int[]? columns;
        readonly uint[]? cells;

        /// <summary>
        /// Initializes a sink projecting onto <paramref name="projection"/>, which the caller has checked against the
        /// scanned table's arity.
        /// </summary>
        /// <param name="inner">The sink receiving the projected rows; copied in, read back with <see cref="Inner"/>.</param>
        /// <param name="projection">The columns, in output order; at least one.</param>
        public ProjectingSink(TSink inner, ReadOnlySpan<int> projection)
        {
            this.inner = inner;
            count = projection.Length;
            c0 = projection[0];
            c1 = count > 1 ? projection[1] : 0;
            c2 = count > 2 ? projection[2] : 0;
            columns = null;
            cells = null;
            if (count > 1)
                cells = ArrayPool<uint>.Shared.Rent(count);

            if (count > 3)
            {
                columns = ArrayPool<int>.Shared.Rent(count);
                projection.CopyTo(columns);
            }
        }

        /// <summary>
        /// Gets the inner sink, with whatever state it accumulated.
        /// </summary>
        public readonly TSink Inner => inner;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            switch (count)
            {
                case 1:
                    inner.AddRow(rowId, row.Slice(c0, 1));
                    return;
                case 2:
                    cells![0] = row[c0];
                    cells[1] = row[c1];
                    break;
                case 3:
                    cells![0] = row[c0];
                    cells[1] = row[c1];
                    cells[2] = row[c2];
                    break;
                default:
                    for (var i = 0; i < count; i++)
                        cells![i] = row[columns![i]];
                    break;
            }

            inner.AddRow(rowId, new ReadOnlySpan<uint>(cells, 0, count));
        }

        /// <summary>
        /// Returns the scratch arrays to the pool. Call once, after the scan.
        /// </summary>
        public readonly void Release()
        {
            if (cells is not null)
                ArrayPool<uint>.Shared.Return(cells);

            if (columns is not null)
                ArrayPool<int>.Shared.Return(columns);
        }

    }

}
