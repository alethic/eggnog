using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Looks up, lane by lane, one column of the row a child context sees by a key of any width: the layered
    /// counterpart of <see cref="SortedKeyNLookup"/>, used for keys of zero or more than three cells. The key is
    /// gathered into a scratch array and probed with <see cref="TableView.TryGetColumn(uint[], int, int, out uint)"/>,
    /// which hashes it once for every layer.
    /// </summary>
    internal readonly struct LayeredKeyNLookup : ILaneProducer
    {

        readonly TableView view;
        readonly ArgGather key;
        readonly int column;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="view">The context's view of the table; its key has as many columns as <paramref name="key"/> has
        /// operands.</param>
        /// <param name="key">Gathers the key.</param>
        /// <param name="column">The column to read.</param>
        public LayeredKeyNLookup(TableView view, ArgGather key, int column)
        {
            this.view = view;
            this.key = key;
            this.column = column;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryProduce(int lane, out uint value)
        {
            key.Load(lane);
            return view.TryGetColumn(key.Cells, 0, column, out value);
        }

    }

}
