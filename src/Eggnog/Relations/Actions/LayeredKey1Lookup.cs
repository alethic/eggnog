using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Looks up, lane by lane, one column of the row a child context sees by a one-cell key (colored-contexts spec §6.5,
    /// X25): the layered counterpart of <see cref="SortedKey1Lookup"/>, probing <see cref="TableView.TryGetColumn(uint, int, out uint)"/>
    /// with the key in registers. No virtual call per lane.
    /// </summary>
    internal readonly struct LayeredKey1Lookup : ILaneProducer
    {

        readonly TableView view;
        readonly ArgSource k0;
        readonly int column;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="view">The context's view of the table; it has one key column.</param>
        /// <param name="k0">The key.</param>
        /// <param name="column">The column to read.</param>
        public LayeredKey1Lookup(TableView view, ArgSource k0, int column)
        {
            this.view = view;
            this.k0 = k0;
            this.column = column;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryProduce(int lane, out uint value) => view.TryGetColumn(k0.Get(lane), column, out value);

    }

}
