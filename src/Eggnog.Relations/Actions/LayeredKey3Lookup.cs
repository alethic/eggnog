using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Looks up, lane by lane, one column of the row a child context sees by a three-cell key (colored-contexts spec
    /// §6.5, X25): the layered counterpart of <see cref="SortedKey3Lookup"/>, probing
    /// <see cref="TableView.TryGetColumn(uint, uint, uint, int, out uint)"/> with the key in registers.
    /// </summary>
    internal readonly struct LayeredKey3Lookup : ILaneProducer
    {

        readonly TableView view;
        readonly ArgSource k0;
        readonly ArgSource k1;
        readonly ArgSource k2;
        readonly int column;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="view">The context's view of the table; it has three key columns.</param>
        /// <param name="k0">The first key cell.</param>
        /// <param name="k1">The second key cell.</param>
        /// <param name="k2">The third key cell.</param>
        /// <param name="column">The column to read.</param>
        public LayeredKey3Lookup(TableView view, ArgSource k0, ArgSource k1, ArgSource k2, int column)
        {
            this.view = view;
            this.k0 = k0;
            this.k1 = k1;
            this.k2 = k2;
            this.column = column;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryProduce(int lane, out uint value) => view.TryGetColumn(k0.Get(lane), k1.Get(lane), k2.Get(lane), column, out value);

    }

}
