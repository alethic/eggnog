using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The read-only face of a <see cref="Database"/> that execution states see: egglog's <c>DbView</c>
    /// (core-relations/src/action/mod.rs) as returned by <c>Database::read_only_view</c>
    /// (core-relations/src/free_join/mod.rs:452-461). Tables, their metadata and indexes, counters, external functions
    /// and the notification list; nothing that merges, adds or clears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The view reflects the database as it is now. While the database merges a stratum or rebuilds against the
    /// union-find, it takes those tables out of its table map (egglog's <c>tables.take</c>), and the view does not see
    /// them: <see cref="GetTable"/> and <see cref="GetTableInfo"/> throw for them, as indexing egglog's
    /// <c>table_info</c> panics. That is what keeps a merge function from reading a table that is being merged
    /// (declare it a read dependency instead). Staging writes goes through <see cref="Catalog"/>, which keeps every
    /// table: writes staged into a table being merged stay in the state's buffers until the state flushes after the
    /// merge (egglog pre-allocates buffers for declared write dependencies to the same end).
    /// </para>
    /// <para>
    /// One view per database, created with it. Reads are safe from any number of threads while the database is not
    /// changing its table map; counters may be advanced concurrently.
    /// </para>
    /// <para>
    /// <see cref="ExecutionState"/>s created over the view find it through <see cref="ExecutionState.View"/>. Because
    /// the state's clone constructor does not know the view, a clone recovers it from its catalog through
    /// <see cref="TryGetForCatalog"/>; every database registers its catalog here.
    /// </para>
    /// </remarks>
    internal sealed class DatabaseView
    {

        /// <summary>
        /// The view of each database, by the catalog it stages through; weak in the catalog, so a dropped database
        /// takes its entry with it.
        /// </summary>
        static readonly ConditionalWeakTable<TableCatalog, DatabaseView> byCatalog = new();

        readonly Database database;

        /// <summary>
        /// Whether the database is a child context's, fixed at construction (the child's owner is set before its view
        /// is created), so the root's table reads pay one field test.
        /// </summary>
        readonly bool isChild;

        /// <summary>
        /// Initializes the view of <paramref name="database"/> and registers it under the database's catalog.
        /// </summary>
        /// <param name="database">The database.</param>
        internal DatabaseView(Database database)
        {
            this.database = database;
            isChild = database.IsChild;
            byCatalog.Add(database.Catalog, this);
        }

        /// <summary>
        /// Gets the database the view reads. For the database's own operations; code running in an execution state
        /// should keep to the view's members.
        /// </summary>
        internal Database Owner => database;

        /// <summary>
        /// Gets the catalog writes are staged through: every table of the database, and the notification list that
        /// staging notifies.
        /// </summary>
        public TableCatalog Catalog => database.Catalog;

        /// <summary>
        /// Gets the tables notified of staged writes since the database last merged. egglog's
        /// <c>notification_list</c>.
        /// </summary>
        public NotificationList<TableId> Notifications => database.Catalog.Notifications;

        /// <summary>
        /// Gets the table with id <paramref name="table"/>. egglog's <c>ExecutionState::get_table</c>. Reading a table
        /// while rules run can break seminaive evaluation; merge functions may read their declared read dependencies.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <returns>The table.</returns>
        /// <exception cref="KeyNotFoundException">No such table, or the database has taken it out.</exception>
        /// <exception cref="InvalidOperationException">The view is a child context's: a child's tables are layered, and
        /// read through its views (colored-contexts spec §3.4, §6.5).</exception>
        public Table GetTable(TableId table)
        {
            if (isChild)
                ThrowLayered(table);

            return database.GetTableInfo(table).Table;
        }

        /// <summary>
        /// Gets the table with id <paramref name="table"/> and its metadata.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <returns>The entry.</returns>
        /// <exception cref="KeyNotFoundException">No such table, or the database has taken it out.</exception>
        public TableInfo GetTableInfo(TableId table) => database.GetTableInfo(table);

        /// <summary>
        /// Gets the table with id <paramref name="table"/> and its metadata, if the view sees it.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <param name="info">The entry.</param>
        /// <returns><c>false</c> if there is no such table or the database has taken it out.</returns>
        public bool TryGetTableInfo(TableId table, [MaybeNullWhen(false)] out TableInfo info) => database.TryGetTableInfo(table, out info);

        /// <summary>
        /// Gets the human-readable name of <paramref name="table"/>, if it has one. egglog's <c>table_name</c>.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <returns>The name, or <c>null</c>.</returns>
        public string? GetTableName(TableId table) => database.GetTableName(table);

        /// <summary>
        /// Gets the ids of every table the view sees, in ascending order. egglog's <c>table_ids</c>. In a child
        /// context's view, every table of the store's schema. Allocates an enumerator; cold path.
        /// </summary>
        public IEnumerable<TableId> TableIds => database.VisibleTableIds();

        /// <summary>
        /// Gets the representation of the store's clock counter, which execution states over this view pin to their
        /// timestamp (C1), or −1 when the database has no clock.
        /// </summary>
        public int ClockCounterRep => database.ClockCounterRep;

        /// <summary>
        /// Increments <paramref name="counter"/> and returns its previous value. egglog's <c>inc_counter</c>.
        /// Thread-safe. In a child context's view, the store's id counter is redirected to the child's id blocks
        /// (<see cref="Database.IncrementCounterInView"/>, colored-contexts spec §4.2): one null test at the root.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value before the increment.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long IncrementCounter(CounterId counter) => database.IncrementCounterInView(counter);

        /// <summary>
        /// Increments <paramref name="counter"/> on the shared counters, never redirected to a child's id blocks: for
        /// ids that must not be local to any context, such as interned container ids (§4.2). Thread-safe.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value before the increment.</returns>
        public long IncrementSharedCounter(CounterId counter) => database.IncrementSharedCounter(counter);

        /// <summary>
        /// Gets the current value of <paramref name="counter"/>. egglog's <c>read_counter</c>. Thread-safe.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadCounter(CounterId counter) => database.Counters.Read(counter);

        /// <summary>
        /// Gets the external function with id <paramref name="function"/>. egglog's <c>external_funcs[func]</c>.
        /// </summary>
        /// <param name="function">The function id.</param>
        /// <returns>The function.</returns>
        /// <exception cref="KeyNotFoundException">No such function.</exception>
        public ExternalFunction GetExternalFunction(ExternalFunctionId function) => database.ExternalFunctions[function];

        /// <summary>
        /// Gets the view of the database whose catalog is <paramref name="catalog"/>, if any: how a cloned execution
        /// state, which keeps only its catalog, finds its view again. O(1) expected, lock-free.
        /// </summary>
        /// <param name="catalog">A catalog.</param>
        /// <param name="view">The view.</param>
        /// <returns><c>false</c> if the catalog belongs to no database (a bare catalog, as in table tests).</returns>
        public static bool TryGetForCatalog(TableCatalog catalog, [MaybeNullWhen(false)] out DatabaseView view)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            return byCatalog.TryGetValue(catalog, out view);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowLayered(TableId table)
        {
            throw new InvalidOperationException($"Table {table} is layered in a child context and cannot be read as one table; read it through the context's view.");
        }

    }

}
