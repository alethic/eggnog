using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="IRowSink"/> that records a copy of every row it is given, with its row id: the closures egglog's
    /// table tests pass to <c>scan_generic</c>.
    /// </summary>
    readonly struct RowCollector : IRowSink
    {

        /// <summary>
        /// Initializes a new instance with an empty list.
        /// </summary>
        public RowCollector()
        {
            Rows = new List<(int Id, uint[] Row)>();
        }

        /// <summary>
        /// Gets the rows received so far.
        /// </summary>
        public List<(int Id, uint[] Row)> Rows { get; }

        /// <inheritdoc />
        public void AddRow(int rowId, ReadOnlySpan<uint> row) => Rows.Add((rowId, row.ToArray()));

    }

}
