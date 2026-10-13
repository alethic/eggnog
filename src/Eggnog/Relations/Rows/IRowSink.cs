using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Receives rows during a scan: egglog's <c>RowSink</c> (core-relations/src/row_buffer/mod.rs:98-105), the output
    /// of <c>Table::scan_project</c> and the rebuild scans.
    /// </summary>
    /// <remarks>
    /// egglog passes sinks as <c>&amp;mut dyn RowSink</c>, a virtual call per row. Here scan methods are generic over
    /// <c>TSink : struct, IRowSink</c> and take the sink by reference, so the JIT specializes the scan per sink and
    /// the per-row call is direct and inlinable. <see cref="TaggedRowSink"/> adapts a <see cref="TaggedRowBuffer"/>,
    /// the sink egglog uses.
    /// </remarks>
    internal interface IRowSink
    {

        /// <summary>
        /// Adds a row that came from row <paramref name="rowId"/> of the scanned table.
        /// </summary>
        /// <param name="rowId">The row's id in its source.</param>
        /// <param name="row">The row's (projected) cells.</param>
        void AddRow(int rowId, ReadOnlySpan<uint> row);

    }

}
