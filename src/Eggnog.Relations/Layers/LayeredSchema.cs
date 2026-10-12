using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The store-wide schema every context reads instead of the root database's table map (colored-contexts spec
    /// §6.1, X16, X39): one <see cref="LayeredTableSchema"/> per function table, the union-find's id, and the
    /// designated contradiction table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Children never touch the root database's <c>tables</c> map or catalog, which the root changes in place while it
    /// merges; they reach root tables only through this schema. <see cref="Register"/> runs under the store's
    /// definition lock and publishes copy-on-write arrays with volatile writes, so every read here is lock-free and
    /// safe while a table is added (a table may be added at any time, X16). Ids are the root database's table ids;
    /// slot 0 is the union-find, which has no <see cref="LayeredTableSchema"/>.
    /// </para>
    /// <para>
    /// It also holds the root's side of layering (§6.1): the root's retraction guard (<see cref="RootGuard"/>), the
    /// root's single-layer view of every table (<see cref="RootView"/>, which children whose ancestors never touched a
    /// table share), and the compaction gate of every shared root table, which keeps the root from renumbering rows its
    /// descendants hold cursors and hidden sets over (§6.8).
    /// </para>
    /// </remarks>
    internal sealed class LayeredSchema
    {

        readonly Store store;
        readonly object sync = new();

        /// <summary>
        /// The schema of each table by index; null for the union-find and for ids not registered. Its length is the
        /// number of table ids. Replaced whole, never changed in place.
        /// </summary>
        LayeredTableSchema?[] byId;

        /// <summary>
        /// Every registered id, ascending. Replaced whole, never changed in place.
        /// </summary>
        TableId[] functionTables;

        /// <summary>
        /// The root's view of each table by index, built on first use and published with a compare-exchange; replaced
        /// with frozen views by <see cref="FreezeRoot"/>. Grown copy-on-write, geometrically, by <see cref="Register"/>.
        /// </summary>
        TableView?[] rootViews;

        /// <summary>
        /// The index of the contradiction table, or −1.
        /// </summary>
        volatile int contradiction = -1;

        /// <summary>
        /// Initializes the schema of <paramref name="store"/>, whose union-find is already its table 0.
        /// </summary>
        /// <param name="store">The store.</param>
        public LayeredSchema(Store store)
        {
            ArgumentNullException.ThrowIfNull(store);
            this.store = store;
            byId = new LayeredTableSchema?[store.UnionFindTable.Index + 1];
            functionTables = Array.Empty<TableId>();
            rootViews = new TableView?[byId.Length];
            RootGuard = new RetractionGuard(this, null);
        }

        /// <summary>
        /// Gets the store the schema belongs to.
        /// </summary>
        internal Store Store => store;

        /// <summary>
        /// Gets the root context's retraction guard (§6.7): the root retracts no row of a shared table while it has live
        /// descendants (decision 1).
        /// </summary>
        public RetractionGuard RootGuard { get; }

        /// <summary>
        /// Gets the union-find table's id, <see cref="Store.UnionFindTable"/>.
        /// </summary>
        public TableId UnionFind => store.UnionFindTable;

        /// <summary>
        /// Gets the designated contradiction table, a nullary relation, or <c>null</c>.
        /// </summary>
        public TableId? Contradiction => contradiction is var c and >= 0 ? new TableId(c) : null;

        /// <summary>
        /// Gets the number of table ids: one more than the largest registered id, the union-find included.
        /// </summary>
        public int TableCount => Volatile.Read(ref byId).Length;

        /// <summary>
        /// Gets the schema of table <paramref name="id"/>, or <c>null</c> for the union-find and for ids that are not
        /// registered. Lock-free, O(1).
        /// </summary>
        /// <param name="id">The table id.</param>
        public LayeredTableSchema? this[TableId id]
        {
            get
            {
                var tables = Volatile.Read(ref byId);
                return (uint)id.Index < (uint)tables.Length ? tables[id.Index] : null;
            }
        }

        /// <summary>
        /// Gets every registered function table, ascending: what the root's rebuild pass rebuilds.
        /// </summary>
        public ReadOnlySpan<TableId> FunctionTables => Volatile.Read(ref functionTables);

        /// <summary>
        /// Enumerates the union-find and every registered table, ascending: a child's <c>table_ids</c>. Reads one
        /// snapshot; allocates an enumerator (cold path).
        /// </summary>
        public IEnumerable<TableId> Ids
        {
            get
            {
                var tables = Volatile.Read(ref byId);
                for (var i = 0; i < tables.Length; i++)
                    if (i == UnionFind.Index || tables[i] is not null)
                        yield return new TableId(i);
            }
        }

        /// <summary>
        /// Gets the spec of table <paramref name="id"/>: <see cref="DisplacedTable.DisplacedSpec"/> for the union-find,
        /// otherwise the registered table's (X39).
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <returns>The spec.</returns>
        /// <exception cref="KeyNotFoundException">The table is neither the union-find nor registered.</exception>
        public TableSpec GetSpec(TableId id)
        {
            if (id == UnionFind)
                return DisplacedTable.DisplacedSpec;

            return Require(id).Spec;
        }

        /// <summary>
        /// Gets the name of table <paramref name="id"/>, or <c>null</c> when it has none (X39).
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <returns>The name.</returns>
        /// <exception cref="KeyNotFoundException">The table is neither the union-find nor registered.</exception>
        public string? GetName(TableId id)
        {
            if (id == UnionFind)
                return Store.UnionFindName;

            return Require(id).Name;
        }

        /// <summary>
        /// Registers the root's table <paramref name="id"/>, which must be a <see cref="SortedWritesTable"/> the root
        /// database has just added. Called by <see cref="Store.AddTable"/> under the definition lock; publishes the new
        /// arrays with volatile writes. O(tables).
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <param name="contextPrivate">Whether the table is context-private.</param>
        /// <exception cref="ArgumentException">The table is the union-find, already registered, or not a sorted-writes
        /// table.</exception>
        public void Register(TableId id, bool contextPrivate)
        {
            var database = store.Root.Database;
            var info = database.GetTableInfo(id);
            if (info.Table is not SortedWritesTable)
                throw new ArgumentException($"Table {id} is not a sorted-writes table; only those can be layered.", nameof(id));

            lock (sync)
            {
                var tables = byId;
                if (id == UnionFind || ((uint)id.Index < (uint)tables.Length && tables[id.Index] is not null))
                    throw new ArgumentException($"Table {id} is already registered.", nameof(id));

                var schema = new LayeredTableSchema(id, info, contextPrivate, database.Dependencies.LevelOf(id));
                var grown = new LayeredTableSchema?[Math.Max(tables.Length, id.Index + 1)];
                Array.Copy(tables, grown, tables.Length);
                grown[id.Index] = schema;

                // §6.8: a shared root table does not compact while the root has live descendants.
                if (contextPrivate == false)
                    schema.Root.CompactionGate = store.Root.Gate;

                // Grown geometrically: a slot beyond the registered ids is never read as a view.
                var views = rootViews;
                if (views.Length < grown.Length)
                {
                    var nextViews = new TableView?[Math.Max(grown.Length, views.Length * 2)];
                    Array.Copy(views, nextViews, views.Length);
                    Volatile.Write(ref rootViews, nextViews);
                }

                // Ids arrive ascending from the root's AddTable, so the new id almost always goes last.
                var functions = functionTables;
                var at = functions.Length;
                while (at > 0 && functions[at - 1].Index > id.Index)
                    at--;

                var nextFunctions = new TableId[functions.Length + 1];
                Array.Copy(functions, nextFunctions, at);
                nextFunctions[at] = id;
                Array.Copy(functions, at, nextFunctions, at + 1, functions.Length - at);

                // The schema first, then the list: a reader that finds the new id in FunctionTables (a volatile read,
                // which acquires) then finds its schema, published before by a volatile write (which releases). The
                // other order lets a lock-free reader see a listed id whose schema is still null (X16).
                Volatile.Write(ref byId, grown);
                Volatile.Write(ref functionTables, nextFunctions);
            }
        }

        /// <summary>
        /// Gets the root's view of table <paramref name="id"/> (§6.1, §6.3): one layer, the root's table, hiding nothing.
        /// Built once and published with a compare-exchange; frozen once the root is (<see cref="FreezeRoot"/>). Children
        /// whose chain never touched the table share it. Lock-free, O(1) once built.
        /// </summary>
        /// <param name="id">A registered function table.</param>
        /// <returns>The view.</returns>
        /// <exception cref="NotSupportedException">The table is the union-find, which has no table view (it is read
        /// through finds and lookups).</exception>
        /// <exception cref="KeyNotFoundException">The table is not registered.</exception>
        public TableView RootView(TableId id)
        {
            if (id == UnionFind)
                throw new NotSupportedException($"The union-find (table {id}) has no table view; read it through Find or lookups.");

            var views = Volatile.Read(ref rootViews);
            if ((uint)id.Index < (uint)views.Length && Volatile.Read(ref views[id.Index]) is { } cached)
                return cached;

            var view = BuildRootView(Require(id), store.Root.IsFrozen);
            if ((uint)id.Index >= (uint)views.Length)
                return view;

            return Interlocked.CompareExchange(ref views[id.Index], view, null) ?? view;
        }

        /// <summary>
        /// Builds the root's view of every registered table as frozen (§8.1 step 5): called by the root's freeze, after
        /// its database froze. Under the schema lock, so a concurrent registration waits.
        /// </summary>
        internal void FreezeRoot()
        {
            lock (sync)
            {
                var views = rootViews;
                foreach (var id in functionTables)
                    Volatile.Write(ref views[id.Index], BuildRootView(byId[id.Index]!, true));
            }
        }

        /// <summary>
        /// Publishes the frozen root view of <paramref name="id"/>, a table just added to a frozen root and frozen
        /// (§4.3 step 4, X43).
        /// </summary>
        /// <param name="id">The table.</param>
        internal void PublishFrozenRootView(TableId id)
        {
            lock (sync)
                Volatile.Write(ref rootViews[id.Index], BuildRootView(Require(id), true));
        }

        /// <summary>
        /// Builds a single-layer view of the root's table.
        /// </summary>
        static TableView BuildRootView(LayeredTableSchema schema, bool isFrozen)
        {
            return new TableView(schema.Id, schema.Spec, [new LayerView(0, schema.RootInfo, HiddenRows.None, isFrozen)]);
        }

        /// <summary>
        /// Designates table <paramref name="id"/> as the store's contradiction table: a registered nullary relation
        /// (no key columns). Once per store.
        /// </summary>
        /// <param name="id">The table id.</param>
        /// <exception cref="InvalidOperationException">A contradiction table is designated already.</exception>
        /// <exception cref="KeyNotFoundException">The table is not registered.</exception>
        /// <exception cref="ArgumentException">The table has key columns.</exception>
        internal void DesignateContradiction(TableId id)
        {
            lock (sync)
            {
                if (contradiction >= 0)
                    throw new InvalidOperationException($"Table {new TableId(contradiction)} is already the store's contradiction table.");

                var schema = Require(id);
                if (schema.KeyCount != 0)
                    throw new ArgumentException($"The contradiction table must be a nullary relation; table {id} has {schema.KeyCount} key columns.", nameof(id));

                schema.MarkContradiction();
                contradiction = id.Index;
            }
        }

        /// <summary>
        /// Gets the schema of a registered table, or throws.
        /// </summary>
        LayeredTableSchema Require(TableId id)
        {
            return this[id] ?? throw new KeyNotFoundException($"Table {id} is not registered with the store.");
        }

    }

}
