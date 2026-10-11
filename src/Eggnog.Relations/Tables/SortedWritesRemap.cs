using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The renumbering callback a <see cref="SortedWritesTable"/> passes to
    /// <see cref="RowBuffer.RemoveStale{TRemap}(ref TRemap)"/> when it compacts: it points the key's index entry at the
    /// row's new id and rebuilds the sort-column runs. egglog's <c>rehash_impl</c> closures
    /// (core-relations/src/table/mod.rs:1205-1234). A struct, so the compaction loop is specialized for it.
    /// </summary>
    internal readonly struct SortedWritesRemap : IRowRemap
    {

        readonly SortedWritesTable table;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table being compacted.</param>
        public SortedWritesRemap(SortedWritesTable table)
        {
            this.table = table;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remap(ReadOnlySpan<uint> row, int oldRow, int newRow)
        {
            table.RemapRow(row, oldRow, newRow);
        }

    }

}
