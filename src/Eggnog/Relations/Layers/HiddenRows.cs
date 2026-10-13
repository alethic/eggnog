using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The rows of one ancestor layer that a view does not see: at most two sealed <see cref="HiddenSet"/>s,
    /// <see cref="Frozen"/> (every frozen level's hides folded into one set, precomputed when the deepest of them
    /// froze) and <see cref="Live"/> (the hides of the levels that are not frozen: one set, or their union). A view's
    /// own layer, and every layer of the root, hide nothing: <see cref="None"/>.
    /// </summary>
    /// <remarks>
    /// A cleared level is not expressed here: it makes the layers below it absent from the view instead. Probing costs at
    /// most two sealed-set probes (<see cref="Contains"/>); a scan walks both with a <see cref="HiddenCursor"/>.
    /// </remarks>
    internal readonly struct HiddenRows
    {

        /// <summary>
        /// Initializes the hidden rows of a layer.
        /// </summary>
        /// <param name="frozen">The frozen levels' hides, sealed, or <c>null</c>.</param>
        /// <param name="live">The other levels' hides, sealed, or <c>null</c>.</param>
        public HiddenRows(HiddenSet? frozen, HiddenSet? live)
        {
            Debug.Assert(frozen is null || frozen.IsSealed, "a view was built over a hidden set that is not sealed");
            Debug.Assert(live is null || live.IsSealed, "a view was built over a hidden set that is not sealed");
            Frozen = frozen;
            Live = live;
        }

        /// <summary>
        /// Gets hidden rows that hide nothing.
        /// </summary>
        public static HiddenRows None => default;

        /// <summary>
        /// Gets the frozen levels' hides, or <c>null</c>.
        /// </summary>
        public HiddenSet? Frozen { get; }

        /// <summary>
        /// Gets the hides of the levels that are not frozen, or <c>null</c>.
        /// </summary>
        public HiddenSet? Live { get; }

        /// <summary>
        /// Gets whether nothing is hidden.
        /// </summary>
        public bool IsNone => Frozen is null && Live is null;

        /// <summary>
        /// Gets whether <paramref name="row"/> is hidden: at most two sealed probes.
        /// </summary>
        /// <param name="row">A row id of the layer.</param>
        /// <returns><c>true</c> if hidden.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(int row) => (Frozen is { } f && f.ContainsSealed(row)) || (Live is { } l && l.ContainsSealed(row));

        /// <summary>
        /// Gets a cursor for a scan in ascending row order.
        /// </summary>
        /// <returns>The cursor.</returns>
        public HiddenCursor Cursor() => new(Frozen, Live);

        /// <summary>
        /// Counts the hidden rows that are live in the layer (a row in both sets once): what a view subtracts from the
        /// layer's live row count. O(hidden rows).
        /// </summary>
        /// <param name="data">The layer's row storage.</param>
        /// <param name="arity">The layer's arity.</param>
        /// <param name="rowCount">The layer's physical row count.</param>
        /// <returns>The number of hidden live rows.</returns>
        internal int CountLive(uint[] data, int arity, int rowCount)
        {
            var count = 0;
            if (Frozen is { } f)
                count += f.CountLive(data, arity, rowCount, null);

            if (Live is { } l)
                count += l.CountLive(data, arity, rowCount, Frozen);

            return count;
        }

    }

}
