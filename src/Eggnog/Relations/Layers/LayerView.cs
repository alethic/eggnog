using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One layer of a <see cref="TableView"/>: the table of one context of the chain, with its own
    /// <see cref="TableInfo"/> (its index catalogs), and the rows of it the view hides. Immutable.
    /// </summary>
    /// <remarks>
    /// The layer's table is live: rows appended to it after the view was built are seen through the view. That is
    /// correct because only the view's own context appends to its own layer, and an ancestor that appends makes the
    /// context not clean until it catches up, which drops its views. The fields are plain readonly fields so compiled
    /// kernels can read them.
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
        /// change, and its cached indexes may be shared lock-free.
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
        /// Keeps the rows of <paramref name="subset"/> the view sees: live and not hidden:
        /// <see cref="Table.RefineLive"/> when the view hides nothing of the layer, else
        /// <see cref="Table.RefineVisible"/> with <see cref="Hidden"/>. O(subset size).
        /// </summary>
        /// <param name="subset">Row ids of the layer, consumed.</param>
        /// <returns>The visible rows, which the caller owns.</returns>
        public Subset RefineVisible(Subset subset) => Hidden.IsNone ? Table.RefineLive(subset) : Table.RefineVisible(subset, Hidden);

        /// <summary>
        /// Gets whether some row of <paramref name="subset"/> is visible to the view: live and not hidden. Stops at the
        /// first one; when the layer has nothing invisible (<see cref="HasInvisible"/>), any row is. O(rows tested),
        /// with the hidden test a <see cref="HiddenCursor"/> walk. egglog takes a nonempty header as proof that a
        /// zero-variable atom holds (core-relations/src/free_join/plan.rs:1502-1505), which tombstones (and, here,
        /// hides) make false; this test looks for a visible row instead.
        /// </summary>
        /// <param name="subset">Row ids of the layer, within its physical rows.</param>
        /// <returns><c>true</c> if a visible row is found.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="subset"/> extends past the layer's rows.</exception>
        public bool AnyVisible(SubsetRef subset)
        {
            if (subset.TryGetBounds(out var bounds) == false || bounds.Start == bounds.End)
                return false;

            var rowCount = Sorted is { } sorted ? sorted.Rows.Count : Table.All().Size;
            if (bounds.Start < 0 || bounds.End > rowCount)
                throw new ArgumentOutOfRangeException(nameof(subset), bounds.End, $"The subset extends past the layer's {rowCount} rows.");

            if (HasInvisible == false)
                return true;

            if (Sorted is not { } table)
                return AnyNotHidden(subset);

            var data = table.Rows.Data;
            var arity = table.Arity;
            var hidden = Hidden.Cursor();
            foreach (var row in subset)
            {
                if (data[row * arity] != Value.StaleRep && hidden.IsHidden(row) == false)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The <see cref="AnyVisible"/> of a layer without tombstones: some row not hidden.
        /// </summary>
        bool AnyNotHidden(SubsetRef subset)
        {
            var hidden = Hidden.Cursor();
            foreach (var row in subset)
            {
                if (hidden.IsHidden(row) == false)
                    return true;
            }

            return false;
        }

    }

}
