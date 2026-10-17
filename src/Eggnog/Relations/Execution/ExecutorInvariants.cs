using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The executor part of the context checker: what the layered executor reads of a context, checked through the same
    /// objects it reads them through, its views (<see cref="TableView"/>, <see cref="LayerView"/>) and the layers'
    /// catalogs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a clean context (<see cref="Context.CheckInvariants"/> runs it after the union-find and tables parts):
    /// <list type="number">
    /// <item><b>View coherence</b>: every view of a registered table has one slot per depth up to the level that
    /// built it, a layer present exactly where the chain has a layer of the table at or above the cleared floor (only the
    /// own layer for a context-private table), each layer view naming that layer's table and catalog entry, and frozen
    /// only if its context is; the hidden rows of each ancestor layer are exactly the hides of the levels above it (every
    /// level's hide is in the view, and every row the view hides is hidden at some level); and the database's view reads
    /// the context's layers at the context's depth and generation.</item>
    /// <item><b>Frozen catalogs current</b>: every index built in the catalog of a frozen layer is current with its
    /// table, since frozen catalogs are never reset.</item>
    /// <item><b>Per-layer sort order</b>: the own layer's sort column is nondecreasing and its run index agrees with
    /// every row (the run of each row's stamp contains the row).</item>
    /// <item><b>Creation stamps</b>: every live own row is stamped at or above the context's creation.</item>
    /// <item><b>Watermark bounds</b>: the semi-naive floor lies between the context's creation and the clock.</item>
    /// <item><b>Canonical view</b>: every id cell of every visible own row is canonical in the context.</item>
    /// <item><b>Unique keys</b>: the view's lookup of every live own row's key finds that row, so no other visible row
    /// holds it.</item>
    /// <item><b>Hidden rows in range</b>: every hidden set a view reads is sealed, of its layer's generation, and names
    /// rows below the layer's physical count.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Cost proportional to the context: O(tables × depth) structure, plus the context's own rows and the hides of
    /// its chain. Ancestors' rows are their own checkers' (a frozen ancestor's were checked when it froze). At a root only
    /// the views and the catalogs are checked: its rows are egglog's, and its settled form is <see cref="Context.CheckRootSettled"/>'s.
    /// </para>
    /// </remarks>
    internal static class ExecutorInvariants
    {

        /// <summary>
        /// Adds every executor invariant <paramref name="context"/> violates to <paramref name="violations"/>.
        /// </summary>
        /// <param name="context">A clean context, entered (or frozen).</param>
        /// <param name="violations">Receives the violations.</param>
        public static void Check(Context context, List<string> violations)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(violations);
            var schema = context.Store.Schema;
            if (context.Tables is not { } tables)
            {
                CheckRoot(context, schema, violations);
                return;
            }

            // Views are built over sealed sets only; the tables part reports an unsealed one.
            if (tables.UnsealedCount != 0)
                return;

            var view = context.Database.View;
            if (ReferenceEquals(view.Source, tables) == false || view.Depth != context.Depth)
                Context.Report(violations, $"view coherence: the database's view does not read the context's layers at depth {context.Depth}");

            if (view.Generation != tables.ViewGeneration)
                Context.Report(violations, $"view coherence: the view's generation {view.Generation} is not the layers' {tables.ViewGeneration}");

            var floor = context.SemiNaiveFloor;
            // The raw counter, not ReadClock: the check also runs once the clock has run out.
            var store = context.Store;
            var clock = store.Counters.Read(store.ClockCounter);
            if (floor < context.CreatedAt || floor > clock)
                Context.Report(violations, $"watermark bounds: the semi-naive floor {floor} lies outside [{context.CreatedAt}, {clock}]");

            foreach (var id in schema.FunctionTables)
            {
                var s = schema[id]!;
                var tableView = tables.GetView(id);
                CheckView(context, tables, s, tableView, violations);
                if (tables.TryGetLayer(id) is { } own)
                    CheckOwnRows(context, s, own, tableView, violations);
            }
        }

        /// <summary>
        /// The root's part: its views read its own tables, and a frozen root's catalogs are current.
        /// </summary>
        static void CheckRoot(Context root, LayeredSchema schema, List<string> violations)
        {
            var view = root.Database.View;
            if (view.IsLayered || view.Source is not null)
                Context.Report(violations, "view coherence: the root's view is layered");

            foreach (var id in schema.FunctionTables)
            {
                var s = schema[id]!;
                var shared = schema.RootView(id);
                var own = view.GetTableView(id);
                foreach (var tableView in new[] { shared, own })
                {
                    if (tableView.LayerCount != 1 || tableView.Layers[0] is not { } layer || ReferenceEquals(layer.Table, s.Root) == false || ReferenceEquals(layer.Info, s.RootInfo) == false)
                    {
                        Context.Report(violations, $"view coherence: the root's view of table {id} is not its own table");
                        continue;
                    }

                    if (layer.Hidden.IsNone == false)
                        Context.Report(violations, $"view coherence: the root's view of table {id} hides rows");
                }

                if (root.IsFrozen)
                    CheckCatalogs(id, 0, s.RootInfo, violations);
            }
        }

        /// <summary>
        /// Checks one view of the context (items 1, 2 and 8).
        /// </summary>
        static void CheckView(Context context, ContextTables tables, LayeredTableSchema s, TableView view, List<string> violations)
        {
            var id = s.Id;
            var depth = context.Depth;
            var chain = context.Ancestors;
            if (view.LayerCount < 1 || view.LayerCount > depth + 1 || view.Id != id)
            {
                Context.Report(violations, $"view coherence: the view of table {id} has {view.LayerCount} layer slots at depth {depth}");
                return;
            }

            // The view is the one built by the deepest level that touched the table; deeper levels share it. A layer
            // created and left empty (a write refused after its buffer was made) changes no view and invalidates none,
            // so only a layer that holds rows, hides or a clear must be in the view.
            var top = view.LayerCount - 1;
            for (var level = top + 1; level <= depth; level++)
                if (s.IsContextPrivate == false && Matters(LayerAt(chain, level, id), level))
                    Context.Report(violations, $"view coherence: the view of table {id} stops at depth {top}, yet the level at depth {level} has a layer");

            var cleared = 0;
            for (var level = top; level > 0; level--)
            {
                if (LayerAt(chain, level, id) is { IsCleared: true })
                {
                    cleared = level;
                    break;
                }
            }

            for (var j = 0; j <= top; j++)
            {
                var expected = j == 0 ? s.Root : LayerAt(chain, j, id)?.Own;
                var expectedInfo = j == 0 ? s.RootInfo : LayerAt(chain, j, id)?.Info;
                var present = expected is not null && j >= cleared && (s.IsContextPrivate == false || j == depth);
                var layer = view.Layers[j];
                if (present && layer is null && j > 0 && Matters(LayerAt(chain, j, id), j) == false)
                    continue;

                if (present != (layer is not null))
                {
                    Context.Report(violations, $"view coherence: the layer at depth {j} of the view of table {id} is {(layer is null ? "absent" : "present")}, expected {(present ? "present" : "absent")}");
                    continue;
                }

                if (layer is null)
                    continue;

                if (ReferenceEquals(layer.Table, expected) == false || ReferenceEquals(layer.Info, expectedInfo) == false || layer.Depth != j)
                    Context.Report(violations, $"view coherence: the layer at depth {j} of the view of table {id} names another table");

                // A view cached before its layer's context froze may still say not frozen, which only forgoes sharing;
                // one that says frozen of a writable layer would share a catalog that merges reset.
                var frozen = chain[j].IsFrozen;
                if (layer.IsFrozen && frozen == false)
                    Context.Report(violations, $"view coherence: the layer at depth {j} of the view of table {id} says frozen, its context is not");

                if (frozen)
                    CheckCatalogs(id, j, layer.Info, violations);

                CheckHidden(chain, top, id, j, (SortedWritesTable)layer.Table, layer.Hidden, violations);
            }
        }

        /// <summary>
        /// Checks the hidden rows a view reads over the layer at depth <paramref name="j"/> (items 1 and 8): sealed, of the
        /// layer's generation, in range, and exactly the hides of the levels in <c>(j, top]</c>.
        /// </summary>
        static void CheckHidden(ReadOnlySpan<Context> chain, int top, TableId id, int j, SortedWritesTable layer, HiddenRows hidden, List<string> violations)
        {
            var rowCount = layer.Rows.Count;
            var generation = layer.Version.Major;
            var rows = new List<int>();
            foreach (var set in new[] { hidden.Frozen, hidden.Live })
            {
                if (set is null)
                    continue;

                if (set.IsSealed == false)
                    Context.Report(violations, $"hidden rows: a hidden set of the view of table {id} over depth {j} is not sealed");

                if (set.Generation != generation)
                    Context.Report(violations, $"hidden rows: a hidden set of the view of table {id} over depth {j} is of generation {set.Generation}, the layer of {generation}");

                rows.Clear();
                set.CopyTo(rows);
                foreach (var r in rows)
                {
                    if ((uint)r >= (uint)rowCount)
                    {
                        Context.Report(violations, $"hidden rows: the view of table {id} hides row {r} of the layer at depth {j}, which has {rowCount} rows");
                        continue;
                    }

                    var hiddenSomewhere = false;
                    for (var level = j + 1; level <= top && hiddenSomewhere == false; level++)
                        hiddenSomewhere = LayerAt(chain, level, id)?.IsHiddenHere(j, r) == true;

                    if (hiddenSomewhere == false)
                        Context.Report(violations, $"view coherence: the view of table {id} hides row {r} of the layer at depth {j}, which no level hides");
                }
            }

            for (var level = j + 1; level <= top; level++)
            {
                if (LayerAt(chain, level, id)?.HiddenOver(j) is not { } set)
                    continue;

                rows.Clear();
                set.CopyTo(rows);
                foreach (var r in rows)
                    if (hidden.Contains(r) == false)
                        Context.Report(violations, $"view coherence: the level at depth {level} hides row {r} of the layer at depth {j} of table {id}, which the view shows");
            }
        }

        /// <summary>
        /// Checks that every index built in a frozen layer's catalogs is current with its table (item 2).
        /// </summary>
        static void CheckCatalogs(TableId id, int j, TableInfo info, List<string> violations)
        {
            foreach (var entry in info.ColumnIndexes.Entries)
                if (entry.Index.TryGet(out var index) && index.NeedsRefresh(info.Table))
                    Context.Report(violations, $"frozen catalogs: a column index of table {id} at depth {j} is behind its frozen table");

            foreach (var entry in info.Indexes.Entries)
                if (entry.Index.TryGet(out var index) && index.NeedsRefresh(info.Table))
                    Context.Report(violations, $"frozen catalogs: a tuple index of table {id} at depth {j} is behind its frozen table");
        }

        /// <summary>
        /// Checks the context's own rows of one table through its view (items 3 to 7).
        /// </summary>
        static void CheckOwnRows(Context context, LayeredTableSchema s, TableLayer own, TableView view, List<string> violations)
        {
            var id = s.Id;
            var depth = context.Depth;
            var table = own.Own;
            var rows = table.Rows;
            var find = context.UnionFind;
            var keyCount = s.KeyCount;
            var previous = 0u;
            for (var r = 0; r < rows.Count; r++)
            {
                var cells = rows.GetRow(r);
                if (s.SortColumn >= 0)
                {
                    var stamp = cells[s.SortColumn];
                    if (stamp < previous)
                        Context.Report(violations, $"sort order: own row {r} of table {id} is stamped {stamp}, below the previous row's {previous}");

                    previous = Math.Max(previous, stamp);
                    if (table.BinarySearchSortValue(stamp, out var start, out var end) == false || r < start || r >= end)
                        Context.Report(violations, $"sort order: the run index of table {id} does not place own row {r} (stamp {stamp}) in its run");
                }

                if (rows.IsStale(r))
                    continue;

                if (s.SortColumn >= 0 && cells[s.SortColumn] < context.CreatedAt)
                    Context.Report(violations, $"creation stamps: own row {r} of table {id} is stamped {cells[s.SortColumn]}, below the context's creation {context.CreatedAt}");

                foreach (var column in s.RebuildColumns)
                {
                    var cell = cells[column.Rep];
                    if (find.Find(cell) != cell)
                        Context.Report(violations, $"canonical view: own row {r} of table {id} names {cell} in column {column.Rep}, which is {find.Find(cell)} here");
                }

                if (view.LayerCount <= depth || view.Layers[depth] is null)
                    continue;

                var found = view.FindRow(cells[..keyCount], out var layer);
                if (found != r || layer != depth)
                    Context.Report(violations, $"unique keys: the view's lookup of own row {r}'s key in table {id} finds row {found} at depth {layer}");
            }
        }

        /// <summary>
        /// Gets whether <paramref name="layer"/> (the layer of the level at <paramref name="level"/>) can change a view:
        /// it holds rows, hides rows of a layer below, or cleared the table. A layer created and left empty cannot, and
        /// views taken before it exists stay correct.
        /// </summary>
        static bool Matters(TableLayer? layer, int level)
        {
            if (layer is null)
                return false;

            if (layer.Own.Rows.Count > 0 || layer.IsCleared)
                return true;

            for (var j = 0; j < level; j++)
                if (layer.HiddenOver(j) is { Count: > 0 })
                    return true;

            return false;
        }

        /// <summary>
        /// Gets the layer the level at <paramref name="level"/> of <paramref name="chain"/> has of table
        /// <paramref name="id"/>, or <c>null</c>; the root has none (its table is the schema's).
        /// </summary>
        static TableLayer? LayerAt(ReadOnlySpan<Context> chain, int level, TableId id) => level == 0 ? null : chain[level].Tables!.TryGetLayer(id);

    }

}
