using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Looks up, lane by lane, one column of a <see cref="SortedWritesTable"/> row by a key of any width: the general
    /// probe of egglog's <c>lookup_row_vectorized</c> (core-relations/src/table_spec.rs:490-507), used for keys of zero or
    /// more than three cells. The key is gathered into a scratch array and probed with
    /// <see cref="SortedWritesTable.TryGetColumn(uint[], int, int, out uint)"/>.
    /// </summary>
    internal readonly struct SortedKeyNLookup : ILaneProducer
    {

        readonly SortedWritesTable table;
        readonly ArgGather key;
        readonly int column;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table; its key has as many columns as <paramref name="key"/> has operands.</param>
        /// <param name="key">Gathers the key.</param>
        /// <param name="column">The column to read.</param>
        public SortedKeyNLookup(SortedWritesTable table, ArgGather key, int column)
        {
            this.table = table;
            this.key = key;
            this.column = column;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryProduce(int lane, out uint value)
        {
            key.Load(lane);
            return table.TryGetColumn(key.Cells, 0, column, out value);
        }

    }

}
