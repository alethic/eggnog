using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A row sink that counts the rows a lift scan delivers.
    /// </summary>
    struct RowCountSink : IRowSink
    {

        /// <summary>
        /// Gets the number of rows delivered.
        /// </summary>
        public int Count { get; private set; }

        /// <inheritdoc />
        public void AddRow(int rowId, ReadOnlySpan<uint> row) => Count++;

    }

}
