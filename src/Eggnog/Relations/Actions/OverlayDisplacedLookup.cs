using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Looks up, lane by lane, one column of a child context's union-find for an id: the <see cref="DisplacedLookup"/>
    /// of an <see cref="OverlayDisplacedTable"/>. Column 1 is answered for every id (its canonical id in the child), so
    /// the bridge's union-find lookup never misses
    /// (<see cref="OverlayDisplacedTable.TryGetColumn(uint, int, out uint)"/>).
    /// </summary>
    internal readonly struct OverlayDisplacedLookup : ILaneProducer
    {

        readonly OverlayDisplacedTable table;
        readonly ArgSource key;
        readonly int column;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The child's union-find table.</param>
        /// <param name="key">The id.</param>
        /// <param name="column">The column to read, 0 to 2.</param>
        public OverlayDisplacedLookup(OverlayDisplacedTable table, ArgSource key, int column)
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
