using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// What rule building, planning and the executor read of a context (colored-contexts spec §9.1, X21, X22, X39): its
    /// view of every table, header evaluation per layer, the planner's statistics, size estimates, the rule set's view
    /// snapshot and the canonical form of Id constants. Every <see cref="Relations.Database"/> has one
    /// (<see cref="Database.View"/>): a root's is made lazily by <see cref="ForRoot"/> and sees each table as one layer,
    /// hiding nothing; a child's is attached with its layers and reads the context's <see cref="ContextTables"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layers. A view of a table has one slot per depth of the chain from the root to the context
    /// (<see cref="TableView"/>); <see cref="LayerCount"/> is the context's depth plus one. At a root
    /// (<see cref="IsLayered"/> false) every table is layer 0 and every member answers exactly as the root database's own
    /// members do (<see cref="ConstraintProcessor.Process"/> on the table's entry, egglog's statistics), so root plans and
    /// headers are egglog's.
    /// </para>
    /// <para>
    /// In a child the members that read rows require the context's views to be current
    /// (<see cref="Context.ThrowIfViewNotCurrent"/>): a context whose own rebuild is pending, or whose ancestors changed
    /// since it caught up, would otherwise plan or snapshot against stale views. Writes staged and not merged do not
    /// matter, as they are invisible until merged. The union-find has no view in a child (X23): asking for it throws
    /// <see cref="NotSupportedException"/>.
    /// </para>
    /// <para>
    /// Cold path: planning and rule-set building. Thread-safe for reads while nothing merges; the root's view cache
    /// publishes with compare-exchange.
    /// </para>
    /// </remarks>
    internal sealed class ContextView : ILayeredHeaderEvaluator, IPlanStatistics
    {

        readonly Database database;
        readonly ILayerViewSource? source;
        readonly Context? context;
        readonly int depth;
        readonly object sync = new();

        /// <summary>
        /// The root's view of each table by index, built on first use; <c>null</c> for a layered view. Entries are only
        /// ever filled, never replaced; growing copies them into a new array.
        /// </summary>
        TableView?[]? rootViews;

        /// <summary>
        /// The snapshot every root rule set shares: a <see cref="ViewSnapshot"/> over <see cref="rootViews"/>, made again
        /// only when that array grows.
        /// </summary>
        ViewSnapshot? rootSnapshot;

        /// <summary>
        /// Initializes a root's view over its own tables.
        /// </summary>
        ContextView(Database root)
        {
            database = root;
            depth = 0;
            rootViews = new TableView?[Math.Max(4, root.TableCount)];
        }

        /// <summary>
        /// Initializes the view of a context whose tables lie in the layers <paramref name="source"/> describes: a child's
        /// <see cref="ContextTables"/>, or a synthetic layer set in executor tests.
        /// </summary>
        /// <param name="database">The database whose rules read through the view.</param>
        /// <param name="source">The layers.</param>
        public ContextView(Database database, ILayerViewSource source)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(source);
            if (source.Depth < 0 || source.Depth >= LayeredSubset.MaxLayers)
                throw new ArgumentOutOfRangeException(nameof(source), source.Depth, $"A context's depth lies in [0, {LayeredSubset.MaxLayers}).");

            this.database = database;
            this.source = source;
            depth = source.Depth;
            context = database.Owner is { IsRoot: false } owner ? owner : null;
        }

        /// <summary>
        /// Creates the view of a root or bare database (§9.1): one layer per table, the database's own entry, hiding
        /// nothing; each table's view is built once and cached (a table's entry is the same object for the database's
        /// life).
        /// </summary>
        /// <param name="root">The database.</param>
        /// <returns>The view.</returns>
        public static ContextView ForRoot(Database root)
        {
            ArgumentNullException.ThrowIfNull(root);
            return new ContextView(root);
        }

        /// <summary>
        /// Gets the database whose rules read through the view.
        /// </summary>
        public Database Database => database;

        /// <summary>
        /// Gets the depth of the context: 0 at a root.
        /// </summary>
        public int Depth => depth;

        /// <summary>
        /// Gets the number of layers a view of a table can have: the depth plus one.
        /// </summary>
        public int LayerCount => depth + 1;

        /// <summary>
        /// Gets whether tables may lie in several layers (a child, or a synthetic layer set deeper than the root).
        /// </summary>
        public bool IsLayered => depth > 0;

        /// <summary>
        /// Gets the layers the view reads, or <c>null</c> at a root.
        /// </summary>
        public ILayerViewSource? Source => source;

        /// <summary>
        /// Gets the database's parallelism.
        /// </summary>
        public ParallelHeuristics Parallelism => database.Parallelism;

        /// <summary>
        /// Gets the database's size estimate: in a child its own layers only (C9, X26).
        /// </summary>
        public long SizeEstimate => database.TotalSizeEstimate;

        /// <summary>
        /// Gets the generation of the views (X21): <see cref="ILayerViewSource.ViewGeneration"/> of a layered context,
        /// which moves whenever cached views are dropped; always 0 at a root, whose views never change structure.
        /// </summary>
        public long Generation => source?.ViewGeneration ?? 0;

        /// <summary>
        /// Gets a stamp of the rows a child context's views show (I29): the sum of the <see cref="Database.ChangeStamp"/>s
        /// of the context and its ancestors (<see cref="Context.ViewStamp"/>), which moves with every merge that adds or
        /// removes rows, every hide, clear and stop anywhere on the chain, so a header subset taken before it moved may
        /// name rows that are gone or moved (a compaction) or miss rows that arrived. Always 0 at a root and for synthetic
        /// layers, whose header subsets are egglog's to keep current. O(depth).
        /// </summary>
        public long Stamp => context?.ViewStamp ?? 0;

        /// <summary>
        /// Gets whether <see cref="TryCanonicalize"/> can answer: a store-backed context, root or child. A bare database
        /// has no designated union-find and runs Id constants as written (X19).
        /// </summary>
        public bool CanCanonicalize => context is not null || database.Owner is { IsRoot: true };

        /// <summary>
        /// Gets the context's view of <paramref name="table"/>: the root's single-layer view at a root; the context's
        /// cached view in a child (<see cref="ContextTables.GetView"/>). O(1) once built.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The view.</returns>
        /// <exception cref="NotSupportedException">This is a child's view and the table is the union-find (X23).</exception>
        /// <exception cref="KeyNotFoundException">No such table.</exception>
        public TableView GetTableView(TableId table) => source is null ? RootView(table) : source.GetView(table);

        /// <summary>
        /// Pins the views of <paramref name="tables"/> for one rule set (§9.1, X21): the cached instances, so the headers
        /// evaluated through this view and the trie roots of the run read the same views (I29). O(tables).
        /// </summary>
        /// <param name="tables">The tables the rule set's atoms and lookups read; repeats are allowed.</param>
        /// <returns>The snapshot.</returns>
        /// <exception cref="InvalidOperationException">This is a child's view, some table is read, and the context is not
        /// current (<see cref="Context.ThrowIfViewNotCurrent"/>).</exception>
        public ViewSnapshot Snapshot(ReadOnlySpan<TableId> tables)
        {
            if (IsLayered == false)
            {
                foreach (var table in tables)
                    RootView(table);

                return SharedRootSnapshot();
            }

            if (tables.IsEmpty)
                return new ViewSnapshot([]);

            ThrowIfStale();
            var max = -1;
            foreach (var table in tables)
                max = Math.Max(max, table.Index);

            var views = new TableView?[max + 1];
            foreach (var table in tables)
                views[table.Index] ??= GetTableView(table);

            return new ViewSnapshot(views);
        }

        /// <summary>
        /// Pins the views a rule set's rules read (§9.1, X21): every atom's table, and, in a layered context, every table an
        /// action looks up (<see cref="RuleSet.LookupTable"/>) except <paramref name="skipLookups"/>; the cached instances, as
        /// <see cref="Snapshot(ReadOnlySpan{TableId})"/> pins them, without collecting the tables first.
        /// </summary>
        /// <remarks>
        /// At a root, whose action lookups read the tables themselves and never the snapshot (§6.5), the rule sets share one
        /// snapshot over the root's view cache (whose entries are only ever filled), so building one allocates nothing once
        /// the views of its tables exist (§14.1). In a layered context, one array per rule set, O(atoms + instructions).
        /// </remarks>
        /// <param name="rules">The rules.</param>
        /// <param name="actions">Their action programs.</param>
        /// <param name="skipLookups">A table whose lookups read no view (a child's union-find, X23), or <c>null</c>.</param>
        /// <returns>The snapshot.</returns>
        /// <exception cref="InvalidOperationException">This is a child's view, some table is read, and the context is not
        /// current.</exception>
        internal ViewSnapshot Snapshot(ReadOnlySpan<RulePlan> rules, DenseIdMap<ActionId, ActionInfo> actions, TableId? skipLookups)
        {
            ArgumentNullException.ThrowIfNull(actions);
            if (IsLayered == false)
            {
                foreach (var rule in rules)
                    foreach (var (_, atom) in rule.Template.Atoms)
                        if (atom.IsDummy == false)
                            RootView(atom.Table);

                return SharedRootSnapshot();
            }

            var max = -1;
            foreach (var rule in rules)
                foreach (var (_, atom) in rule.Template.Atoms)
                    if (atom.IsDummy == false)
                        max = Math.Max(max, atom.Table.Index);

            foreach (var (_, action) in actions)
                foreach (var instr in action.Instrs)
                    if (RuleSet.LookupTable(instr) is { } t && t != skipLookups)
                        max = Math.Max(max, t.Index);

            if (max < 0)
                return new ViewSnapshot([]);

            ThrowIfStale();
            var views = new TableView?[max + 1];
            foreach (var rule in rules)
                foreach (var (_, atom) in rule.Template.Atoms)
                    if (atom.IsDummy == false)
                        views[atom.Table.Index] ??= GetTableView(atom.Table);

            foreach (var (_, action) in actions)
                foreach (var instr in action.Instrs)
                    if (RuleSet.LookupTable(instr) is { } t && t != skipLookups)
                        views[t.Index] ??= GetTableView(t);

            return new ViewSnapshot(views);
        }

        /// <summary>
        /// Gets the canonical form of <paramref name="id"/> in the context (X19): <see cref="DisplacedTable.FindNaive"/>
        /// on the store's union-find at a store-backed root, the context's find in a child.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="canonical">The canonical id, or <paramref name="id"/> when the view cannot canonicalize.</param>
        /// <returns><c>false</c> only for a bare database, which has no designated union-find.</returns>
        public bool TryCanonicalize(uint id, out uint canonical)
        {
            if (context is { } child)
            {
                canonical = child.UnionFind.Find(id);
                return true;
            }

            if (database.Owner is { IsRoot: true } root)
            {
                canonical = root.Store.RootUnionFind.FindNaive(id);
                return true;
            }

            canonical = id;
            return false;
        }

        /// <summary>
        /// Evaluates a header (§9.4): splits <paramref name="constraints"/> into fast and slow ones and returns the rows
        /// matching every fast one, per present layer of the table's view. At a root it is
        /// <see cref="ConstraintProcessor.Process"/> on the table's entry, in layer 0. In a layered context each present
        /// layer is processed over its own entry and cached indexes (a time range is one binary search per layer); a
        /// constraint is fast overall when it is fast in some layer, and the layers where it was slow refine by it
        /// (<see cref="Table.RefineOne"/>). A view with no present layer selects nothing, after classifying the
        /// constraints on the store's schema.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="constraints">The constraints.</param>
        /// <param name="fast">Receives the fast constraints.</param>
        /// <param name="slow">Receives the slow ones.</param>
        /// <returns>The rows, per layer, which the caller owns; they may include tombstones and rows the view hides.</returns>
        /// <exception cref="InvalidOperationException">This is a child's view and the context is not current.</exception>
        /// <exception cref="NotSupportedException">This is a child's view and the table is the union-find (X23).</exception>
        public LayeredSubset Evaluate(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow)
        {
            ArgumentNullException.ThrowIfNull(fast);
            ArgumentNullException.ThrowIfNull(slow);
            ThrowIfStale();
            var view = GetTableView(table);
            if (IsLayered == false)
                return LayeredSubset.Single(0, ConstraintProcessor.Process(view.Layers[0]!.Info, constraints, fast, slow));

            var present = view.PresentMask;
            if (present == 0)
            {
                ConstraintProcessor.Split(ClassificationInfo(table), constraints, fast, slow);
                return LayeredSubset.Empty;
            }

            if ((present & (present - 1)) == 0)
            {
                var only = BitOperations.TrailingZeroCount(present);
                return LayeredSubset.Single(only, ConstraintProcessor.Process(view.Layers[only]!.Info, constraints, fast, slow));
            }

            return EvaluateLayers(view, constraints, fast, slow);
        }

        /// <summary>
        /// Estimates the number of rows of <paramref name="table"/> the context sees, or of those matching
        /// <paramref name="constraint"/>: egglog's <c>estimate_size</c> (core-relations/src/free_join/mod.rs:463-482) on
        /// each present layer, summed, hidden rows included as a root estimate includes tombstones (§3.4).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="constraint">The constraint, or <c>null</c>.</param>
        /// <returns>The estimate, at most <see cref="int.MaxValue"/>.</returns>
        /// <exception cref="NotSupportedException">This is a child's view and the table is the union-find (X23).</exception>
        public int EstimateSize(TableId table, Constraint? constraint)
        {
            var view = GetTableView(table);
            long total = 0;
            foreach (var layer in view.Layers)
                if (layer is not null)
                    total += Database.EstimateTable(layer.Table, constraint);

            return (int)Math.Min(total, int.MaxValue);
        }

        /// <summary>
        /// Gets the approximate number of rows of the atom's table the context sees that match its fast constraints, as
        /// egglog's <c>approx_size</c> counts slots, invisible ones included (§9.7): the size of the header
        /// <see cref="Evaluate"/> computes.
        /// </summary>
        /// <param name="atom">The atom's id.</param>
        /// <param name="info">The atom.</param>
        /// <returns>The estimate.</returns>
        long IPlanStatistics.GetApproximateSize(AtomId atom, QueryAtom info)
        {
            ArgumentNullException.ThrowIfNull(info);
            var fast = new List<Constraint>(info.FastConstraints.Length);
            var slow = new List<Constraint>();
            var subset = Evaluate(info.Table, info.FastConstraints, fast, slow);
            var size = subset.Size;
            subset.Dispose(null);
            return size;
        }

        /// <summary>
        /// Gets the uniqueness estimate of a column as the context sees the table (§9.7): the row counts and the distinct
        /// counts of the present layers (<see cref="ColumnCardEstimator.GetColumnUniqueness(TableInfo, ColumnId)"/>),
        /// summed. At a root, egglog's <c>col_uniqueness</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="column">The column.</param>
        /// <returns>The estimate.</returns>
        ColumnUniqueness IPlanStatistics.GetColumnUniqueness(TableId table, ColumnId column)
        {
            ThrowIfStale();
            var view = GetTableView(table);
            if (view.Layers.Length == 1)
                return ColumnCardEstimator.GetColumnUniqueness(view.Layers[0]!.Info, column);

            ulong rows = 0;
            ulong distinct = 0;
            foreach (var layer in view.Layers)
            {
                if (layer is null)
                    continue;

                var u = ColumnCardEstimator.GetColumnUniqueness(layer.Info, column);
                rows += u.TableSize;
                distinct += u.ColumnSize;
            }

            return new ColumnUniqueness(rows, distinct);
        }

        /// <summary>
        /// Evaluates a header over two or more present layers; see <see cref="Evaluate"/>.
        /// </summary>
        LayeredSubset EvaluateLayers(TableView view, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow)
        {
            var layers = view.Layers;
            var subsets = new Subset[layers.Length];
            var layerFast = new List<Constraint>?[layers.Length];
            var layerSlow = new List<Constraint>();
            for (var l = 0; l < layers.Length; l++)
            {
                if (layers[l] is not { } lv)
                    continue;

                var lf = new List<Constraint>(constraints.Length);
                layerSlow.Clear();
                subsets[l] = ConstraintProcessor.Process(lv.Info, constraints, lf, layerSlow);
                layerFast[l] = lf;
            }

            // Fast overall when fast in some present layer, in the order the constraints were given; every layer shares
            // the table's kind and spec, so the lists agree in practice and no layer refines.
            var firstFast = fast.Count;
            foreach (var constraint in constraints)
            {
                var isFast = false;
                foreach (var lf in layerFast)
                    if (lf is not null && lf.Contains(constraint))
                        isFast = true;

                if (isFast)
                {
                    if (fast.IndexOf(constraint, firstFast) < 0)
                        fast.Add(constraint);
                }
                else
                {
                    slow.Add(constraint);
                }
            }

            for (var l = 0; l < layers.Length; l++)
            {
                if (layerFast[l] is not { } lf)
                    continue;

                for (var i = firstFast; i < fast.Count; i++)
                    if (lf.Contains(fast[i]) == false)
                        subsets[l] = layers[l]!.Table.RefineOne(subsets[l], fast[i]);
            }

            return LayeredSubset.FromLayers(subsets, null);
        }

        /// <summary>
        /// Gets the entry whose kind and spec classify constraints on <paramref name="table"/> when no layer is present:
        /// the root's entry in a store (every layer shares its kind and spec), else the database's own.
        /// </summary>
        TableInfo ClassificationInfo(TableId table)
        {
            if (context is { } child && child.Store.Schema[table] is { } schema)
                return schema.RootInfo;

            return database.GetTableInfo(table);
        }

        /// <summary>
        /// Gets the root's single-layer view of <paramref name="table"/>, building and caching it on first use.
        /// </summary>
        TableView RootView(TableId table)
        {
            var views = Volatile.Read(ref rootViews)!;
            if ((uint)table.Index < (uint)views.Length && Volatile.Read(ref views[table.Index]) is { } cached)
                return cached;

            var view = TableView.ForTable(table, database.GetTableInfo(table), database.IsFrozen);
            lock (sync)
            {
                views = rootViews!;
                if ((uint)table.Index >= (uint)views.Length)
                {
                    var grown = new TableView?[Math.Max(table.Index + 1, views.Length * 2)];
                    Array.Copy(views, grown, views.Length);
                    Volatile.Write(ref rootViews, grown);
                    views = grown;
                }

                return Interlocked.CompareExchange(ref views[table.Index], view, null) ?? view;
            }
        }

        /// <summary>
        /// Gets the snapshot over the root's current view cache, made when the cache array is new. Every view a caller
        /// ensured before the call is in it: entries are only filled, and growing copies them.
        /// </summary>
        ViewSnapshot SharedRootSnapshot()
        {
            var views = Volatile.Read(ref rootViews)!;
            var shared = Volatile.Read(ref rootSnapshot);
            if (shared is not null && shared.Wraps(views))
                return shared;

            shared = new ViewSnapshot(views);
            Volatile.Write(ref rootSnapshot, shared);
            return shared;
        }

        /// <summary>
        /// Throws when this is a child's view and the context's views are not current.
        /// </summary>
        void ThrowIfStale() => context?.ThrowIfViewNotCurrent();

    }

}
