using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class ContextTables
    {

        /// <summary>
        /// The cached views by table index. Grown copy-on-write under the layer lock; a slot is published with a
        /// compare-exchange and dropped by <see cref="InvalidateViews"/>.
        /// </summary>
        TableView?[] views;

        /// <summary>
        /// The tables whose view structure changed since the last invalidation (a layer created, a hide, a clear), by
        /// index; under the layer lock.
        /// </summary>
        bool[] dirty;

        /// <summary>
        /// Whether any <see cref="dirty"/> flag is set; under the layer lock.
        /// </summary>
        bool anyDirty;

        long viewGeneration;

        /// <summary>
        /// Gets a counter that moves whenever cached views are dropped because their structure changed: by the merge
        /// epilogue, a rebuild that caught up with an ancestor, a clear and a freeze. Layer creation never moves it.
        /// </summary>
        public long ViewGeneration => Interlocked.Read(ref viewGeneration);

        /// <summary>
        /// Gets the context's view of <paramref name="table"/>, building and caching it on first use: a table the
        /// context never touched shares its parent's view instance; a context-private table shows the own layer only;
        /// otherwise every ancestor layer not below a cleared level, each with the rows the context does not see, and
        /// the own layer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An ancestor layer's hidden rows are split as in <see cref="HiddenRows"/>: the hides of the frozen levels are
        /// the single set the deepest frozen ancestor folded at its freeze; the hides of the levels that are not frozen
        /// are one set, or their union (<see cref="LayerStatistics.FilterMerges"/>). A frozen context's views fold
        /// everything into the frozen part. A frozen context never caches beyond its arrays: for a table added after
        /// its freeze it returns its parent's view.
        /// </para>
        /// <para>
        /// Requires every hidden set of the chain to be sealed, as it is outside a merge. Cost of a build: O(depth)
        /// plus the unions; a cached view costs one load. Thread-safe.
        /// </para>
        /// </remarks>
        /// <param name="table">A registered function table.</param>
        /// <returns>The view.</returns>
        /// <exception cref="NotSupportedException">The table is the union-find, which a child reads only through
        /// lookups.</exception>
        /// <exception cref="KeyNotFoundException">The table is not registered with the store.</exception>
        public TableView GetView(TableId table)
        {
            if (table == schema.UnionFind)
                throw new NotSupportedException($"The union-find (table {table}) has no table view in a child context; a child reads it only through lookups.");

            var cache = Volatile.Read(ref views);
            var index = table.Index;
            if ((uint)index < (uint)cache.Length && Volatile.Read(ref cache[index]) is { } cached)
                return cached;

            var s = schema[table] ?? throw new KeyNotFoundException($"Table {table} is not registered with the store.");
            if (frozen)
                return (uint)index < (uint)cache.Length ? BuildAndCache(s, cache) : ParentView(table);

            if ((uint)index >= (uint)cache.Length)
            {
                lock (layerLock)
                {
                    if (frozen)
                        return ParentView(table);

                    if (index >= slots.Length)
                        GrowLocked(index);

                    cache = views;
                }
            }

            return BuildAndCache(s, cache);
        }

        /// <summary>
        /// Drops cached views: with <paramref name="all"/>, every one (a rebuild that caught up with an ancestor, a
        /// clear, a freeze); otherwise those of the tables whose structure changed since the last invalidation (the
        /// merge epilogue). Moves <see cref="ViewGeneration"/> when anything changed. Domain thread, outside a search.
        /// Nothing once frozen.
        /// </summary>
        /// <param name="all">Whether to drop every view.</param>
        internal void InvalidateViews(bool all)
        {
            lock (layerLock)
            {
                if (frozen)
                    return;

                if (all)
                {
                    Volatile.Write(ref views, new TableView?[views.Length]);
                    Array.Clear(dirty);
                    anyDirty = false;
                    Interlocked.Increment(ref viewGeneration);
                    return;
                }

                if (anyDirty == false)
                    return;

                var cache = views;
                for (var i = 0; i < dirty.Length; i++)
                {
                    if (dirty[i] == false)
                        continue;

                    dirty[i] = false;
                    if (i < cache.Length)
                        Volatile.Write(ref cache[i], null);
                }

                anyDirty = false;
                Interlocked.Increment(ref viewGeneration);
            }
        }

        /// <summary>
        /// Marks <paramref name="table"/>'s view structure changed.
        /// </summary>
        void MarkDirty(TableId table)
        {
            lock (layerLock)
                MarkDirtyLocked(table);
        }

        /// <summary>
        /// <see cref="MarkDirty"/> under the layer lock.
        /// </summary>
        void MarkDirtyLocked(TableId table)
        {
            if (frozen)
                return;

            if (table.Index >= dirty.Length)
                GrowLocked(table.Index);

            dirty[table.Index] = true;
            anyDirty = true;
        }

        /// <summary>
        /// Builds the view of <paramref name="schema"/>'s table and publishes it in <paramref name="cache"/>, keeping a view
        /// another thread published first.
        /// </summary>
        TableView BuildAndCache(LayeredTableSchema schema, TableView?[] cache)
        {
            var view = BuildView(schema);
            return Interlocked.CompareExchange(ref cache[schema.Id.Index], view, null) ?? view;
        }

        /// <summary>
        /// Gets the parent's view of <paramref name="table"/>: the root's view, or the parent's cached one.
        /// </summary>
        TableView ParentView(TableId table) => depth == 1 ? schema.RootView(table) : chain[depth - 1].Tables!.GetView(table);

        /// <summary>
        /// Builds the context's view of a table; see <see cref="GetView"/>.
        /// </summary>
        TableView BuildView(LayeredTableSchema s)
        {
            var table = s.Id;
            var own = TryGetLayer(table);
            if (s.IsContextPrivate)
            {
                // Descendants never read a context-private table: the view is the own layer, or nothing.
                var privateLayers = new LayerView?[depth + 1];
                if (own is not null)
                    privateLayers[depth] = new LayerView(depth, own.Info, HiddenRows.None, frozen);

                return new TableView(table, s.Spec, privateLayers);
            }

            // A context hides nothing of a table it never touched.
            if (own is null)
                return ParentView(table);

            var layers = new LayerView?[depth + 1];
            layers[depth] = new LayerView(depth, own.Info, HiddenRows.None, frozen);
            var floor = ClearedFloor(table);
            var frozenTop = frozen ? depth : DeepestFrozenAncestor();
            List<HiddenSet>? live = null;
            for (var j = floor; j < depth; j++)
            {
                if (AncestorInfo(j, table) is not { } info)
                    continue;

                var rowCount = ((SortedWritesTable)info.Table).Rows.Count;
                HiddenSet? frozenPart = null;
                if (frozenTop > j)
                    frozenPart = frozenTop == depth ? FoldAtFreeze(table, j, own, rowCount) : FrozenFold(frozenTop, table, j);

                // The levels that are not frozen, above the layer: one set is used as it is, more are unioned.
                (live ??= new List<HiddenSet>()).Clear();
                for (var level = Math.Max(j, frozenTop) + 1; level <= depth; level++)
                    if (LayerAt(level, table)?.HiddenOver(j) is { Count: > 0 } set)
                        live.Add(set);

                HiddenSet? livePart = null;
                if (live.Count == 1)
                {
                    livePart = live[0];
                }
                else if (live.Count > 1)
                {
                    livePart = HiddenSet.Union(live.ToArray(), rowCount);
                    statistics.FilterMerges++;
                }

                layers[j] = new LayerView(j, info, new HiddenRows(frozenPart, livePart), j == 0 ? schema.Store.Root.IsFrozen : chain[j].IsFrozen);
            }

            return new TableView(table, s.Spec, layers);
        }

        /// <summary>
        /// Gets the hides of the frozen levels <c>(layer, frozenTop]</c> over the layer at depth <paramref name="layer"/>:
        /// the single set the frozen ancestor at <paramref name="frozenTop"/> folded into its view at its freeze.
        /// </summary>
        HiddenSet? FrozenFold(int frozenTop, TableId table, int layer)
        {
            Debug.Assert(frozenTop > 0 && chain[frozenTop].IsFrozen);
            var view = chain[frozenTop].Tables!.GetView(table);

            // A frozen ancestor without a layer of the table shares the view of the deepest level that has one; the
            // levels between have no layer, so they hide nothing, and that view's fold is the fold up to frozenTop.
            return layer < view.LayerCount ? view.Layers[layer]?.Hidden.Frozen : null;
        }

        /// <summary>
        /// Gets the fold a freezing context stores for the layer at depth <paramref name="layer"/>: its parent's frozen
        /// fold together with its own hides of that layer, as one sealed set.
        /// </summary>
        HiddenSet? FoldAtFreeze(TableId table, int layer, TableLayer own, int rowCount)
        {
            var parentFold = depth - 1 > layer ? FrozenFold(depth - 1, table, layer) : null;
            var ownSet = own.HiddenOver(layer) is { Count: > 0 } set ? set : null;
            if (parentFold is null)
                return ownSet;

            if (ownSet is null)
                return parentFold;

            statistics.FilterMerges++;
            return HiddenSet.Union([parentFold, ownSet], rowCount);
        }

        /// <summary>
        /// Gets the depth of the deepest frozen ancestor (frozen contexts form a prefix of every chain), or −1 when the
        /// root is not frozen. O(depth).
        /// </summary>
        int DeepestFrozenAncestor()
        {
            var top = -1;
            for (var level = 0; level < depth && chain[level].IsFrozen; level++)
                top = level;

            return top;
        }

    }

}
