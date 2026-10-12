using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Receives the renumbering of live rows while <see cref="RowBuffer.RemoveStale{TRemap}(ref TRemap)"/> compacts a
    /// buffer: egglog's <c>remap: impl FnMut(&amp;[Value], RowId, RowId)</c> (core-relations/src/row_buffer/mod.rs:357).
    /// </summary>
    /// <remarks>
    /// Implement it on a struct and pass it by reference: the compaction is generic over the struct, so the JIT
    /// specializes it and can inline the callback, as Rust monomorphizes the closure.
    /// </remarks>
    internal interface IRowRemap
    {

        /// <summary>
        /// Called once for every live row, in increasing <paramref name="oldRow"/> order, after the row has moved to
        /// <paramref name="newRow"/>. Must not modify the buffer being compacted.
        /// </summary>
        /// <param name="row">The row's cells, at its new position.</param>
        /// <param name="oldRow">The row's id before compaction.</param>
        /// <param name="newRow">The row's id after compaction; never greater than <paramref name="oldRow"/>.</param>
        void Remap(ReadOnlySpan<uint> row, int oldRow, int newRow);

    }

}
