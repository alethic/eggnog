using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// A row sink that counts the rows a scan emits and sums one of their cells: the cheapest sink that still reads
    /// every emitted row, so a scan benchmark measures the scan loop and the result cannot be optimized away.
    /// </summary>
    /// <remarks>
    /// A struct, passed by reference, so the scan is specialized for it and the per-row call inlines (as the executor's
    /// sinks are).
    /// </remarks>
    internal struct CountingSink : IRowSink
    {

        readonly int column;

        /// <summary>
        /// Initializes a sink that sums <paramref name="column"/>.
        /// </summary>
        /// <param name="column">The column to sum; below the scanned table's arity.</param>
        public CountingSink(int column)
        {
            this.column = column;
            Count = 0;
            Sum = 0;
        }

        /// <summary>
        /// Gets the number of rows received.
        /// </summary>
        public long Count { get; private set; }

        /// <summary>
        /// Gets the sum of the summed column over the rows received.
        /// </summary>
        public long Sum { get; private set; }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            Count++;
            Sum += row[column];
        }

    }

}
