using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The database's side of colored contexts: which <see cref="Context"/> owns it, the store clock it pins, the id
    /// blocks a child mints from, its change stamp, and the child constructor. A bare database (no <see cref="Store"/>)
    /// has no owner, no clock and no blocks, and behaves exactly as egglog's.
    /// </summary>
    internal sealed partial class Database
    {

        /// <summary>
        /// The context that owns the database: the root context for a store's root database, the child for a child's;
        /// <c>null</c> for a bare database (and for a clone).
        /// </summary>
        Context? owner;

        /// <summary>
        /// The id blocks every term mint goes through in a child; <c>null</c> at the root and in a bare database, which
        /// mint directly from their counters.
        /// </summary>
        IdBlockAllocator? idBlocks;

        /// <summary>
        /// The representation of the store's clock counter, which <see cref="ExecutionState.ReadCounter"/> pins to the
        /// state's timestamp; −1 when the database has no designated clock.
        /// </summary>
        int clockRep = -1;

        /// <summary>
        /// The representation of the store's id counter, whose mints <see cref="IncrementCounterInView"/> bounds by
        /// <see cref="UnionFind.MaxId"/> at the root; −1 when the database has no designated id counter.
        /// </summary>
        int idRep = -1;

        /// <summary>
        /// The change stamp: moved by every merge that adds or removes rows and by every clear that empties a table.
        /// </summary>
        long changeStamp;

        /// <summary>
        /// A child's layering engine, which its catalog's resolver asks for the table a write is staged into; <c>null</c>
        /// at the root and in a bare database.
        /// </summary>
        ContextTables? layers;

        /// <summary>
        /// Whether the database is frozen. Written last by <see cref="Freeze"/>.
        /// </summary>
        volatile bool frozen;

        /// <summary>
        /// The number of tables whose compaction <see cref="Freeze"/> skipped because live descendants held their row
        /// ids. Written by <see cref="Freeze"/> before <see cref="frozen"/>.
        /// </summary>
        int compactionsBlockedAtFreeze;

        /// <summary>
        /// What rule building, planning and the executor read: a child's, attached with its layers; a root's or bare
        /// database's, made on first use of <see cref="View"/>.
        /// </summary>
        ContextView? contextView;

        /// <summary>
        /// Initializes a child context's database: its own catalog, notification list, table map and size estimate,
        /// sharing the root's counters, external functions, dependency graph, container environments and base value
        /// registry by reference.
        /// </summary>
        /// <remarks>
        /// The table map holds the child's union-find (its <see cref="OverlayDisplacedTable"/>) at the store's
        /// union-find id, and the child's own layers as they are adopted (<see cref="AdoptLayer"/>). Writes are staged
        /// through the catalog's resolver, which hands out the child's union-find for the union-find id and the child's
        /// layer of any other table, created on first use (<see cref="ContextTables.ResolveForStaging"/>). The context
        /// attaches its layering right after construction (<see cref="AttachLayers"/>). The root's container
        /// environments and base value registry are created here if they did not exist, so both share one instance: a
        /// value the root boxed unboxes in the child, and a type or value registered through either is seen by both
        /// (the registry is thread-safe, so concurrent children may share it too).
        /// </remarks>
        /// <param name="root">The store's root database.</param>
        /// <param name="owner">The child context.</param>
        /// <param name="unionFind">The child's union-find table.</param>
        /// <param name="ids">The child's id blocks.</param>
        /// <param name="parallelism">The child's parallelism: the child's option, or by default serial under a frozen
        /// parent and the parent's heuristics otherwise.</param>
        internal Database(Database root, Context owner, Table unionFind, IdBlockAllocator ids, ParallelHeuristics parallelism)
        {
            ArgumentNullException.ThrowIfNull(root);
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(unionFind);
            ArgumentNullException.ThrowIfNull(ids);
            ArgumentNullException.ThrowIfNull(parallelism);

            this.parallelism = parallelism;
            this.owner = owner;
            idBlocks = ids;
            clockRep = root.clockRep;
            idRep = root.idRep;
            catalog = new TableCatalog(lazyNotifications: true) { Resolver = ResolveForStaging };
            tables = new DenseIdMap<TableId, TableInfo>();
            tables.Insert(owner.Store.UnionFindTable, new TableInfo(Store.UnionFindName, unionFind, ParallelHeuristics.Serial));
            counters = root.counters;
            externalFunctions = root.externalFunctions;
            deps = root.deps;
            containers = root.Containers;
            baseValues = root.BaseValues;
            view = new DatabaseView(this);
        }

        /// <summary>
        /// Gets the context that owns the database, or <c>null</c> for a bare database.
        /// </summary>
        internal Context? Owner => owner;

        /// <summary>
        /// Gets whether this is a child context's database, whose schema questions are answered from the store's
        /// <see cref="LayeredSchema"/> and whose definitions and rebuilds go through the store and the context.
        /// </summary>
        internal bool IsChild => owner is { IsRoot: false };

        /// <summary>
        /// Gets the representation of the store's clock counter, or −1 when the database has none. An execution state
        /// reading this counter gets its own timestamp.
        /// </summary>
        internal int ClockCounterRep => clockRep;

        /// <summary>
        /// Gets the number of tables that kept their tombstones when the database froze, because live descendants held
        /// cursors or hidden sets over their row ids and compaction would have renumbered the rows
        /// (<see cref="SortedWritesTable.ForceRehash"/>). A frozen table never changes again, so those tombstones stay
        /// for the database's life. 0 before the freeze and when nothing was blocked.
        /// </summary>
        internal int CompactionsBlockedAtFreeze => Volatile.Read(ref compactionsBlockedAtFreeze);

        /// <summary>
        /// Gets the change stamp: moved by merges that add or remove rows, by clears that empty a table, by hides and
        /// by the contradiction stop; no-ops and redundant unions leave it alone. <see cref="Context.Version"/> reads
        /// it.
        /// </summary>
        internal long ChangeStamp => Interlocked.Read(ref changeStamp);

        /// <summary>
        /// Gets a child's layering engine, or <c>null</c> at the root and in a bare database.
        /// </summary>
        internal ContextTables? Layers => layers;

        /// <summary>
        /// Gets or sets the guard this database's retractions go through: the root's
        /// <see cref="LayeredSchema.RootGuard"/>, a child's <see cref="ContextTables.Guard"/>; <c>null</c> in a bare
        /// database. <see cref="ClearTable"/> consults it; execution states and the buffers of <see cref="NewBuffer"/>
        /// take <see cref="GuardForNewState"/>.
        /// </summary>
        internal RetractionGuard? RetractionGuard { get; set; }

        /// <summary>
        /// Gets whether the database is frozen: its tables are sealed and its indexes refresh serially; merges,
        /// rebuilds, clears, rule runs and new buffers throw.
        /// </summary>
        public bool IsFrozen => frozen;

        /// <summary>
        /// Gets the guard an execution state or a buffer of <see cref="NewBuffer"/> created now keeps: none in a bare
        /// database (one null test per staged removal there), otherwise <see cref="RetractionGuard"/>, which decides
        /// when a removal is staged.
        /// </summary>
        /// <remarks>
        /// A store's root hands out its guard even while it has no live descendants. The state of a write batch or a
        /// rule run cannot outlive a child's creation (structural calls are refused re-entrantly), but a state from
        /// <see cref="CreateExecutionState"/> or a buffer from <see cref="NewBuffer"/> may outlive a
        /// <see cref="Context.CreateChild"/>, and without the guard would then delete rows the new child reads. The
        /// guard's checks start with one volatile read of the root's gate and return at once while the root has no live
        /// descendants (<see cref="RetractionGuard.CheckRemove"/>).
        /// </remarks>
        /// <returns>The guard, or <c>null</c>.</returns>
        internal RetractionGuard? GuardForNewState() => owner is null ? null : RetractionGuard;

        /// <summary>
        /// Gets the view rules are built, planned and run through: a child's reads its layers; a root's or bare
        /// database's, made on first use (<see cref="ContextView.ForRoot"/>), sees each table as one layer and answers
        /// as the database's own members do.
        /// </summary>
        internal ContextView View => Volatile.Read(ref contextView) ?? CreateRootView();

        /// <summary>
        /// Gets the view when tables may lie in several layers (a child, or a synthetic layer set attached in executor
        /// tests), else <c>null</c>: the switch of the layered planning and query members. One field load at a root.
        /// </summary>
        internal ContextView? LayeredView => Volatile.Read(ref contextView) is { IsLayered: true } layered ? layered : null;

        /// <summary>
        /// Attaches a child's layering engine, which the catalog's resolver and the merge epilogue use, makes its guard
        /// the database's, and attaches the view over it. Once.
        /// </summary>
        /// <param name="tables">The child's layering.</param>
        /// <exception cref="InvalidOperationException">This is not a child's database, or layers are attached already.</exception>
        internal void AttachLayers(ContextTables tables)
        {
            ArgumentNullException.ThrowIfNull(tables);
            if (IsChild == false || layers is not null)
                throw new InvalidOperationException("Layers are attached once, to a child context's database.");

            layers = tables;
            RetractionGuard = tables.Guard;
            AttachView(tables);
        }

        /// <summary>
        /// Attaches the view of <paramref name="source"/>'s layers as the database's <see cref="View"/>: a child's
        /// <see cref="ContextTables"/>, or, in executor tests, a synthetic layer set over a bare database whose own
        /// tables are the layers' layer 0 (their specs and names answer schema questions). Before any view is made.
        /// Once.
        /// </summary>
        /// <param name="source">The layers.</param>
        /// <exception cref="InvalidOperationException">The database already has a view, or is a store's root.</exception>
        internal void AttachView(ILayerViewSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (owner is { IsRoot: true })
                throw new InvalidOperationException("A store's root database sees its own tables; its view is made on first use.");

            var view = new ContextView(this, source);
            if (Interlocked.CompareExchange(ref contextView, view, null) is not null)
                throw new InvalidOperationException("The database already has a view.");
        }

        /// <summary>
        /// Makes the root's or bare database's view on first use of <see cref="View"/>.
        /// </summary>
        ContextView CreateRootView()
        {
            var view = ContextView.ForRoot(this);
            return Interlocked.CompareExchange(ref contextView, view, null) ?? view;
        }

        /// <summary>
        /// Puts a child's new layer into the table map, so its merges, rebuilds and index resets visit it. On the
        /// domain thread, outside a search: at creation, or at the next merge round for a layer created during a search
        /// (<see cref="ContextTables.AdoptDeferred"/>).
        /// </summary>
        /// <param name="layer">The layer.</param>
        internal void AdoptLayer(TableLayer layer)
        {
            ArgumentNullException.ThrowIfNull(layer);
            var had = tables.Insert(layer.Id, layer.Info);
            Debug.Assert(had == false, "a layer was adopted twice");
            layer.SetAdopted();
        }

        /// <summary>
        /// Gets whether the child's layer of <paramref name="table"/> is adopted and taken out of the table map by a
        /// merge in progress: a write-phase read of it is a read of the stratum being merged. Exact whenever the write
        /// phase asks, since every layer is adopted before a merge round starts.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns><c>true</c> if the layer is taken out.</returns>
        internal bool IsTakenOut(TableId table) => layers?.TryGetLayer(table) is { IsAdopted: true } && tables.ContainsKey(table) == false;

        /// <summary>
        /// Moves the change stamp for a change of a child's layering that no merge reports: a hide, the contradiction
        /// stop.
        /// </summary>
        internal void NoteLayerChange() => BumpChangeStamp();

        /// <summary>
        /// Clears a child's own layer for <see cref="ContextTables.ClearTable"/>: its rows and staged writes, its
        /// cached indexes, the size estimate, and the change stamp.
        /// </summary>
        /// <param name="info">The layer's entry.</param>
        internal void ClearOwnLayer(TableInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            var previous = info.Table.Count;
            info.Table.Clear();
            info.ResetIndexes();
            totalSizeEstimate -= previous;
            BumpChangeStamp();
        }

        /// <summary>
        /// Attaches a store's root database to its root context and designates the store's id counter and clock.
        /// </summary>
        /// <param name="root">The root context.</param>
        /// <param name="ids">The store's id counter.</param>
        /// <param name="clock">The store's clock counter.</param>
        internal void AttachRoot(Context root, CounterId ids, CounterId clock)
        {
            ArgumentNullException.ThrowIfNull(root);
            owner = root;
            idRep = ids.Rep;
            clockRep = clock.Rep;
        }

        /// <summary>
        /// Increments <paramref name="counter"/> for a term mint seen from this database: in a child, the store's id
        /// counter is redirected to the child's id blocks, so every id the child mints is local to it; any other
        /// counter, and every counter at the root, increments directly. At a store's root a mint from the id counter is
        /// bounded by <see cref="UnionFind.MaxId"/>, as the blocks are, so a rule's mint past it fails at the mint rather
        /// than when a union-find grows to the id. One null test and one compare against a constant at the root.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value before the increment, or the id minted from a block.</returns>
        /// <exception cref="OverflowException">The counter is the store's id counter and has no id left.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal long IncrementCounterInView(CounterId counter)
        {
            if (idBlocks is { } blocks && counter == blocks.Counter)
                return blocks.Next();

            var value = counters.Increment(counter);
            if ((ulong)value > UnionFind.MaxId && counter.Rep == idRep)
                Store.ThrowIdsExhausted(value);

            return value;
        }

        /// <summary>
        /// Increments <paramref name="counter"/> on the shared counters, never redirected to id blocks: for ids that
        /// must not be local to any context, such as globally interned container ids.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value before the increment.</returns>
        internal long IncrementSharedCounter(CounterId counter) => counters.Increment(counter);

        /// <summary>
        /// Gets the spec of <paramref name="table"/>: from the store's schema in a child, whose table map holds only
        /// its own layers; from the table map at the root and in a bare database.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The spec.</returns>
        /// <exception cref="KeyNotFoundException">No such table (or, at the root, it is taken out).</exception>
        internal TableSpec GetTableSpec(TableId table) => IsChild ? owner!.Store.Schema.GetSpec(table) : GetTableInfo(table).Spec;

        /// <summary>
        /// Gets the name of <paramref name="table"/>, or <c>null</c>: from the store's schema in a child, from the
        /// table map at the root and in a bare database.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The name.</returns>
        /// <exception cref="KeyNotFoundException">No such table (or, at the root, it is taken out).</exception>
        internal string? GetTableName(TableId table) => IsChild ? owner!.Store.Schema.GetName(table) : GetTableInfo(table).Name;

        /// <summary>
        /// Resolves an optional timestamp parameter: the given value, or else the store clock's current value, or 0 for
        /// a database without a clock (a bare database).
        /// </summary>
        /// <param name="timestamp">The timestamp passed, or <c>null</c>.</param>
        /// <returns>The timestamp to use.</returns>
        /// <exception cref="OverflowException">The clock has reached <see cref="uint.MaxValue"/> (<see cref="Store.ReadClock"/>).</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint ResolveTimestamp(uint? timestamp) => timestamp ?? CurrentClock();

        /// <summary>
        /// Gets the store clock's current value, or 0 without a clock. Checked as <see cref="Store.ReadClock"/> is: the
        /// clock never yields <see cref="Value.StaleRep"/>.
        /// </summary>
        /// <exception cref="OverflowException">The clock has reached <see cref="uint.MaxValue"/>.</exception>
        uint CurrentClock() => clockRep < 0 ? 0u : Store.CheckEpoch(counters.Read(new CounterId(clockRep)));

        /// <summary>
        /// Moves the change stamp. Thread-safe: the tables of one stratum may merge in parallel.
        /// </summary>
        void BumpChangeStamp() => Interlocked.Increment(ref changeStamp);

        /// <summary>
        /// Gets the entry of <paramref name="table"/> that the single-table query members read
        /// (<see cref="ProcessConstraints(TableId, ReadOnlySpan{Constraint}, List{Constraint}, List{Constraint})"/>,
        /// <see cref="EstimateSize"/>, <see cref="ColumnCardEstimator"/>): the table map's entry, at the cost of one owner
        /// test.
        /// </summary>
        /// <remarks>
        /// A child answers those questions through its view (<see cref="View"/>): its entry for a table is its own
        /// layer (and for the union-find its own union-find layer), not what it sees, and its rows lie in several
        /// layers, which no single subset describes. So every table is refused here in a child: context = copy, a child
        /// never answers a query from a layer as if it were its view, and a rule in a child never reads the union-find
        /// as an atom (only lookups reach it).
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <returns>The entry.</returns>
        /// <exception cref="KeyNotFoundException">No such table, or it is taken out.</exception>
        /// <exception cref="NotSupportedException">This is a child context's database: <paramref name="table"/> is the
        /// store's union-find, or a function table, which is answered through <see cref="View"/>.</exception>
        internal TableInfo GetQueryTableInfo(TableId table)
        {
            if (IsChild)
                ThrowQueriedInChild(table);

            return GetTableInfo(table);
        }

        /// <summary>
        /// Throws when this is a store's database, root or child, for <see cref="AddTable"/>: every table of a store is
        /// added through <see cref="Store.AddTable"/>, which registers it with the schema children read and the root's
        /// rebuild walks.
        /// </summary>
        void ThrowIfStoreDefinition()
        {
            if (owner is not null)
                ThrowChildDefinition();
        }

        /// <summary>
        /// Throws when this is a child's database, for definitions, which only the store adds.
        /// </summary>
        void ThrowIfChildDefinition()
        {
            if (IsChild)
                ThrowChildDefinition();
        }

        /// <summary>
        /// Throws when this is a child's database, for the public rebuild entry points: a child rebuilds through
        /// <see cref="Context"/>, whose layers call the internal table operations.
        /// </summary>
        /// <param name="member">The member called.</param>
        void ThrowIfChildRebuild(string member)
        {
            if (IsChild)
                throw new InvalidOperationException($"{member} cannot be called on a child context's database; a child rebuilds through its context.");
        }

        /// <summary>
        /// The staging resolver of a child's catalog: the child's union-find for the union-find id, otherwise the
        /// child's layer of the table, created on first use.
        /// </summary>
        /// <param name="table">The table staged into.</param>
        /// <returns>The table writes are staged into.</returns>
        /// <exception cref="InvalidOperationException">No layering is attached yet, or the layer cannot be created
        /// (<see cref="ContextTables.GetOrCreateLayer"/>).</exception>
        Table ResolveForStaging(TableId table)
        {
            if (layers is not { } l)
                throw new InvalidOperationException("The child context's layers are not attached yet.");

            return l.ResolveForStaging(table);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowChildDefinition()
        {
            throw new InvalidOperationException("Definitions are added through the Store.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowQueriedInChild(TableId table)
        {
            if (table == owner!.Store.UnionFindTable)
                throw new NotSupportedException($"The union-find (table {table}) cannot be an atom of a query in a child context; a child reads it only through lookups.");

            throw new NotSupportedException($"Table {table} lies in several layers in a child context; read it through the database's View (ContextView), which evaluates headers, statistics and estimates per layer.");
        }

    }

}
