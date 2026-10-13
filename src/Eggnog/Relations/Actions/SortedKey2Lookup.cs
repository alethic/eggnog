using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Looks up, lane by lane, one column of a <see cref="SortedWritesTable"/> row by a two-cell key: one probe of egglog's
    /// <c>lookup_row_vectorized</c> (core-relations/src/table_spec.rs:490-507), whose per-lane <c>get_row_column</c> is
    /// monomorphized for the table type. The key cells are passed in registers to
    /// <see cref="SortedWritesTable.TryGetColumn(uint, uint, int, out uint)"/>, so the probe hashes and compares
    /// without reading the key from memory.
    /// </summary>
    internal readonly struct SortedKey2Lookup : ILaneProducer
    {

        readonly SortedWritesTable table;
        readonly ArgSource k0;
        readonly ArgSource k1;
        readonly int column;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table; it has two key columns.</param>
        /// <param name="k0">The first key cell.</param>
        /// <param name="k1">The second key cell.</param>
        /// <param name="column">The column to read.</param>
        public SortedKey2Lookup(SortedWritesTable table, ArgSource k0, ArgSource k1, int column)
        {
            this.table = table;
            this.k0 = k0;
            this.k1 = k1;
            this.column = column;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryProduce(int lane, out uint value) => table.TryGetColumn(k0.Get(lane), k1.Get(lane), column, out value);

    }

}
