using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Looks up, lane by lane, one column of the union-find table's row for an id: egglog's
    /// <c>lookup_row_vectorized</c> (core-relations/src/table_spec.rs:490-507) monomorphized for <c>DisplacedTable</c>.
    /// Column 1 is answered for every id (its canonical id), so the bridge's union-find lookup never misses
    /// (<see cref="DisplacedTable.TryGetColumn(uint, int, out uint)"/>).
    /// </summary>
    internal readonly struct DisplacedLookup : ILaneProducer
    {

        readonly DisplacedTable table;
        readonly ArgSource key;
        readonly int column;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The union-find table.</param>
        /// <param name="key">The id.</param>
        /// <param name="column">The column to read, 0 to 2.</param>
        public DisplacedLookup(DisplacedTable table, ArgSource key, int column)
        {
            this.table = table;
            this.key = key;
            this.column = column;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryProduce(int lane, out uint value) => table.TryGetColumn(key.Get(lane), column, out value);

    }

}
