using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One layer of a <see cref="TableView"/> (colored-contexts spec §6.3): the table of one context of the chain,
    /// with its own <see cref="TableInfo"/> (its index catalogs), and the rows of it the view hides. Immutable.
    /// </summary>
    /// <remarks>
    /// The layer's table is live: rows appended to it after the view was built are seen through the view. That is
    /// correct because only the view's own context appends to its own layer, and an ancestor that appends makes the
    /// context not clean until it catches up, which drops its views (§6.3 invalidation). The fields are plain readonly
    /// fields so compiled kernels can read them (§9.9).
    /// </remarks>
    internal sealed class LayerView
    {

        /// <summary>
        /// The depth of the context whose layer this is: 0 for the root's table.
        /// </summary>
        public readonly int Depth;

        /// <summary>
        /// The layer's entry in its context's database: the table and its cached indexes.
        /// </summary>
        public readonly TableInfo Info;

        /// <summary>
        /// The layer's table, <see cref="TableInfo.Table"/> of <see cref="Info"/>.
        /// </summary>
        public readonly Table Table;

        /// <summary>
        /// The rows of the layer the view hides; <see cref="HiddenRows.None"/> for the view's own layer.
        /// </summary>
        public readonly HiddenRows Hidden;

        /// <summary>
        /// Whether the layer's context was frozen when the view was built: its table, indexes and hidden sets never
        /// change, and its cached indexes may be shared lock-free (§9.5).
        /// </summary>
        public readonly bool IsFrozen;

        /// <summary>
        /// The layer's table as a <see cref="SortedWritesTable"/>, for non-virtual point lookups; <c>null</c> for another
        /// table kind.
        /// </summary>
        internal readonly SortedWritesTable? Sorted;

        /// <summary>
        /// Initializes a layer view.
        /// </summary>
        /// <param name="depth">The depth of the layer's context.</param>
        /// <param name="info">The layer's entry in its context's database.</param>
        /// <param name="hidden">The rows the view hides.</param>
        /// <param name="isFrozen">Whether the layer's context is frozen.</param>
        public LayerView(int depth, TableInfo info, HiddenRows hidden, bool isFrozen)
        {
            ArgumentNullException.ThrowIfNull(info);
            Depth = depth;
            Info = info;
            Table = info.Table;
            Hidden = hidden;
            IsFrozen = isFrozen;
            Sorted = info.Table as SortedWritesTable;
        }

        /// <summary>
        /// Gets whether some physical row of the layer is invisible to the view (a tombstone or a hidden row): what
        /// <c>HasStaleRows</c> answers for a single table.
        /// </summary>
        public bool HasInvisible => Table.HasStaleRows || Hidden.IsNone == false;

        /// <summary>
        /// Keeps the rows of <paramref name="subset"/> the view sees: live and not hidden. O(subset size).
        /// </summary>
        /// <param name="subset">Row ids of the layer, consumed.</param>
        /// <returns>The visible rows, which the caller owns.</returns>
        public Subset RefineVisible(Subset subset)
        {
            if (Hidden.IsNone)
                return Table.RefineLive(subset);

            if (Sorted is { } sorted)
            {
                var filter = new VisibleRowFilter(sorted, Hidden);
                subset.Retain(ref filter);
                return subset;
            }

            var live = Table.RefineLive(subset);
            var hiddenOnly = new HiddenOnlyFilter(Hidden);
            live.Retain(ref hiddenOnly);
            return live;
        }

    }

}
