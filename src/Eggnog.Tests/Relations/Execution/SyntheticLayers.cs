using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A layered context made by hand for the layered executor's tests: a chain of databases, one per depth, whose
    /// tables are the layers of each table's view, with any rows, tombstones and hidden rows a test asks for. Attached
    /// to the depth-0 database (<see cref="Database.AttachView"/>), whose own tables are every view's layer 0, so rules
    /// built and run there plan, evaluate headers and join through these views.
    /// </summary>
    /// <remarks>
    /// The views are built once (<see cref="Seal"/>) and never change; <see cref="ViewGeneration"/> is 0, and
    /// <see cref="Find"/> is the identity (no union-find). A layer can be left out of a view (no layer at that depth, or a
    /// level that cleared it), and a view's layers can hide any of their rows, split between a frozen and a live set.
    /// </remarks>
    sealed class SyntheticLayers : ILayerViewSource
    {

        readonly Database[] levels;
        readonly Dictionary<TableId, (bool[] Present, List<int>?[] Frozen, List<int>?[] Live)> layout = new();
        readonly Dictionary<TableId, TableView> views = new();
        readonly Dictionary<TableId, TableView> fixedViews = new();

        /// <summary>
        /// Initializes the layers <paramref name="levels"/>: the database at index <c>d</c> holds the layers at depth
        /// <c>d</c>; index 0 is the database rules run in.
        /// </summary>
        /// <param name="levels">The databases, at least two; each has the same tables at the same ids.</param>
        public SyntheticLayers(Database[] levels)
        {
            ArgumentNullException.ThrowIfNull(levels);
            if (levels.Length < 2)
                throw new ArgumentException("A layered context has a root and at least one more level.", nameof(levels));

            this.levels = levels;
        }

        /// <inheritdoc />
        public int Depth => levels.Length - 1;

        /// <inheritdoc />
        public long ViewGeneration => 0;

        /// <summary>
        /// Gets the database of the layers at depth <paramref name="depth"/>.
        /// </summary>
        public Database Level(int depth) => levels[depth];

        /// <summary>
        /// Sets which layers of <paramref name="table"/> its view shows and which of their rows it hides (by row id,
        /// split into the frozen and the live part).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="present">Whether the layer at each depth is in the view.</param>
        /// <param name="frozen">The rows each layer's frozen part hides, or <c>null</c>.</param>
        /// <param name="live">The rows each layer's live part hides, or <c>null</c>.</param>
        public void Define(TableId table, bool[] present, List<int>?[] frozen, List<int>?[] live)
        {
            layout[table] = (present, frozen, live);
        }

        /// <summary>
        /// Makes <paramref name="view"/> the view of <paramref name="table"/> as it is: for a table no level layers, such as
        /// a union-find, seen through a one-layer view of any database (<see cref="TableView.ForTable"/>).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="view">Its view.</param>
        public void Use(TableId table, TableView view)
        {
            fixedViews[table] = view;
        }

        /// <summary>
        /// Builds every view; call after the last write to any level.
        /// </summary>
        public void Seal()
        {
            views.Clear();
            foreach (var (table, view) in fixedViews)
                views[table] = view;

            foreach (var (table, (present, frozen, live)) in layout)
            {
                var layers = new LayerView?[levels.Length];
                for (var d = 0; d < levels.Length; d++)
                {
                    if (present[d] == false)
                        continue;

                    var info = levels[d].GetTableInfo(table);
                    var sorted = (SortedWritesTable)info.Table;
                    layers[d] = new LayerView(d, info, new HiddenRows(Set(sorted, frozen[d]), Set(sorted, live[d])), d < levels.Length - 1);
                }

                views[table] = new TableView(table, levels[0].GetTableInfo(table).Spec, layers);
            }
        }

        /// <inheritdoc />
        public TableView GetView(TableId table) => views[table];

        /// <inheritdoc />
        public uint Find(uint id) => id;

        /// <summary>
        /// Builds a sealed hidden set over <paramref name="rows"/> of <paramref name="layer"/>, or <c>null</c> for none.
        /// </summary>
        static HiddenSet? Set(SortedWritesTable layer, List<int>? rows)
        {
            if (rows is null || rows.Count == 0)
                return null;

            var count = layer.Rows.Count;
            var set = new HiddenSet(layer.Version.Major);
            foreach (var row in rows.Distinct())
                set.Add(row, count);

            set.Seal(count);
            return set;
        }

    }

}
