using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// A row sink that copies every row it receives into a list: how the root benchmarks read a table's live rows when
    /// they check an iteration's result. Allocates one array per row, so it is for checks outside the measurement only.
    /// </summary>
    internal readonly struct RowCollector : IRowSink
    {

        /// <summary>
        /// Initializes a collector with an empty list.
        /// </summary>
        public RowCollector()
        {
            Rows = new List<uint[]>();
        }

        /// <summary>
        /// Gets the rows received so far, in scan order.
        /// </summary>
        public List<uint[]> Rows { get; }

        /// <inheritdoc />
        public void AddRow(int rowId, ReadOnlySpan<uint> row) => Rows.Add(row.ToArray());

        /// <summary>
        /// Lists the live rows of <paramref name="table"/>, in row order: a scan of <see cref="Table.All"/>, which skips
        /// tombstones. O(physical rows).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The live rows, copied.</returns>
        public static List<uint[]> LiveRows(Table table)
        {
            ArgumentNullException.ThrowIfNull(table);

            var collector = new RowCollector();
            var all = table.All();
            table.ScanGeneric(all.AsRef(), ref collector);
            all.Dispose();
            return collector.Rows;
        }

    }

}
