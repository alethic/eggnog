using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A collection of tables and the machinery around them: egglog's <c>Database</c>
    /// (core-relations/src/free_join/mod.rs:286-821). It owns the table catalog with each table's cached indexes, the
    /// merge-dependency strata, the notification list of tables with staged writes, the counters and the external
    /// functions, and it drives merging to a fixpoint and rebuilding against the union-find.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writing. Writes are staged, never applied directly: through <see cref="NewBuffer"/> (which notifies the table),
    /// through an <see cref="ExecutionState"/> (rules, merge functions, <see cref="CreateExecutionState"/>), or by a
    /// table's own rebuild. <see cref="MergeAll"/> then merges every notified table, which may stage more writes (a
    /// union-id merge stages unions into the union-find), and repeats until nothing is notified. It always merges in
    /// dependency-stratum order: egglog's small-batch path <c>merge_simple</c> merges in notification order and ignores
    /// strata (mod.rs:593-605), which with parallel <c>run_on_tables</c> notifying in completion order lets a merge read
    /// a table whose writes are not merged yet (map §8.1 row 11). See <see cref="MergeAll"/>.
    /// </para>
    /// <para>
    /// Rebuilding. <see cref="ApplyRebuild"/> stages, for every table of a list, the rewrite of its id columns through
    /// the union-find's rebuilder, with the union-find taken out of the catalog meanwhile, and merges.
    /// <see cref="RefreshRowsForValues"/> re-stamps rows that mention ids whose meaning changed in place. The bridge
    /// alternates the two until neither changes anything.
    /// </para>
    /// <para>
    /// Reading. <see cref="GetTable"/>, <see cref="EstimateSize"/>,
    /// <see cref="ProcessConstraints(TableId, ReadOnlySpan{Constraint})"/> and the cached indexes of
    /// <see cref="TableInfo"/> may run on many threads at once while nothing merges. Execution states see the database
    /// through <see cref="ReadOnlyView"/>.
    /// </para>
    /// <para>
    /// Failures. A merge function reports a failure through <see cref="ExecutionState.ReportFailure"/> (where egglog's
    /// bridge calls a panic function that writes its side channel). The database collects the first such failure from
    /// every state it creates while merging and rebuilding, in <see cref="Failure"/>, until <see cref="TakeFailure"/>.
    /// An exception thrown by a table's merge propagates; the database stays consistent: every table is back in the
    /// catalog, the unmerged tables of the interrupted round stay notified, and the caches are reset.
    /// </para>
    /// <para>
    /// Threading: methods that change the database (adding tables, counters or functions, merging, rebuilding,
    /// clearing, cloning) need exclusive access, egglog's <c>&amp;mut self</c>. Staging and counter increments may run
    /// concurrently with reads. Parallelism follows <see cref="Parallelism"/>: database-level operations go parallel
    /// above <see cref="ParallelHeuristics.DbLevelThreshold"/> rows in total.
    /// </para>
    /// </remarks>
    internal sealed partial class Database
    {

        readonly ParallelHeuristics parallelism;
        readonly TableCatalog catalog;
        readonly DenseIdMap<TableId, TableInfo> tables;
        readonly CounterSet counters;
        readonly ExternalFunctionRegistry externalFunctions;
        readonly DependencyGraph deps;
        readonly DatabaseView view;
        long totalSizeEstimate;
        string? failure;

        /// <summary>
        /// Initializes an empty database. egglog's <c>Database::new</c>.
        /// </summary>
        /// <param name="parallelism">When database-level operations go parallel, and the shard count of cached indexes;
        /// <see cref="ParallelHeuristics.Default"/> if <c>null</c>.</param>
        public Database(ParallelHeuristics? parallelism = null)
        {
            this.parallelism = parallelism ?? ParallelHeuristics.Default;
            catalog = new TableCatalog();
            tables = new DenseIdMap<TableId, TableInfo>();
            counters = new CounterSet();
            externalFunctions = new ExternalFunctionRegistry();
            deps = new DependencyGraph();
            view = new DatabaseView(this);
        }

        /// <summary>
        /// Initializes a deep copy of <paramref name="source"/>, for <see cref="Clone"/>.
        /// </summary>
        Database(Database source)
        {
            parallelism = source.parallelism;
            catalog = new TableCatalog();
            tables = new DenseIdMap<TableId, TableInfo>(source.tables.IdCount);
            for (var i = 0; i < source.tables.IdCount; i++)
            {
                var id = new TableId(i);
                var info = source.GetTableInfo(id).Clone();
                var added = catalog.Add(info.Table);
                Debug.Assert(added == id, "the catalog and the table map disagree on ids");
                tables.Insert(id, info);
            }

            counters = source.counters.Clone();
            externalFunctions = source.externalFunctions.Clone();
            deps = source.deps.Clone();
            totalSizeEstimate = source.totalSizeEstimate;
            view = new DatabaseView(this);

            // The container environments (declared in Containers/Database.Containers.cs): egglog's derived Clone copies
            // container_values (core-relations/src/free_join/mod.rs:289-300). Table and counter ids carry over, so the
            // copied merge functions and the rebuild tracker stay valid for the copy.
            containers = source.containers?.Clone();

            // The cloned tables carry their staged writes (a sorted-writes table copies them), so the copy must merge
            // the same tables. egglog's clone shares the notification list instead.
            foreach (var table in source.catalog.Notifications.Reset())
            {
                source.catalog.Notifications.Notify(table);
                catalog.Notifications.Notify(table);
            }
        }

        /// <summary>
        /// Gets when database-level operations go parallel.
        /// </summary>
        public ParallelHeuristics Parallelism => parallelism;

        /// <summary>
        /// Gets the read-only view that execution states run against. egglog's <c>read_only_view</c>. The same object
        /// for the life of the database.
        /// </summary>
        public DatabaseView ReadOnlyView => view;

        /// <summary>
        /// Gets the catalog that writes are staged through. It holds every table, including the ones taken out of the
        /// table map while merging or rebuilding, and the notification list.
        /// </summary>
        internal TableCatalog Catalog => catalog;

        /// <summary>
        /// Gets the counters.
        /// </summary>
        internal CounterSet Counters => counters;

        /// <summary>
        /// Gets the external functions.
        /// </summary>
        internal ExternalFunctionRegistry ExternalFunctions => externalFunctions;

        /// <summary>
        /// Gets the merge dependencies of the tables.
        /// </summary>
        internal DependencyGraph Dependencies => deps;

        /// <summary>
        /// Gets the rough total size of the database (the sum of the tables' live row counts after the last
        /// <see cref="MergeAll"/>, adjusted by <see cref="MergeTable"/> and <see cref="ClearTable"/>), which decides
        /// whether database-level operations go parallel. egglog's <c>total_size_estimate</c>.
        /// </summary>
        public long TotalSizeEstimate => totalSizeEstimate;

        /// <summary>
        /// Gets the id the next <see cref="AddTable"/> returns, for tables whose merge functions refer to their own id
        /// ("knot tying"). egglog's <c>next_table_id</c>.
        /// </summary>
        public TableId NextTableId => tables.NextId;

        /// <summary>
        /// Gets the number of tables.
        /// </summary>
        public int TableCount => tables.IdCount;

        /// <summary>
        /// Gets the first failure a merge function reported while the database merged or rebuilt since the last
        /// <see cref="TakeFailure"/>, or <c>null</c>.
        /// </summary>
        public string? Failure => Volatile.Read(ref failure);

        /// <summary>
        /// Adds <paramref name="table"/> and returns its id. egglog's <c>add_table</c> and <c>add_table_named</c>
        /// (mod.rs:633-674). The table's stratum follows from its read dependencies (<see cref="DependencyGraph"/>).
        /// Requires exclusive access.
        /// </summary>
        /// <param name="table">The table; not already part of this database.</param>
        /// <param name="readDependencies">The tables its merge function reads; already added.</param>
        /// <param name="writeDependencies">The tables its merge function only writes.</param>
        /// <param name="name">The table's human-readable name, or <c>null</c>.</param>
        /// <returns>The table's id, <see cref="NextTableId"/>.</returns>
        /// <exception cref="ArgumentException">The table is already in the database, or a read dependency is not.</exception>
        public TableId AddTable(Table table, ReadOnlySpan<TableId> readDependencies = default, ReadOnlySpan<TableId> writeDependencies = default, string? name = null)
        {
            ArgumentNullException.ThrowIfNull(table);
            foreach (var (_, existing) in tables)
                if (ReferenceEquals(existing.Table, table))
                    throw new ArgumentException("The table is already part of this database.", nameof(table));

            var id = tables.NextId;
            Debug.Assert(catalog.Count == tables.IdCount, "the catalog and the table map disagree on ids");
            deps.AddTable(id, readDependencies, writeDependencies);
            var added = catalog.Add(table);
            Debug.Assert(added == id, "the catalog and the table map disagree on ids");
            tables.Insert(id, new TableInfo(name, table, parallelism));
            return id;
        }

        /// <summary>
        /// Gets the table with id <paramref name="table"/>. egglog's <c>get_table</c> and <c>get_table_mut</c>.
        /// </summary>
        /// <remarks>
        /// Staging into a buffer from <see cref="Table.NewBuffer"/> does not notify the database, so the next
        /// <see cref="MergeAll"/> will not merge it; use <see cref="NewBuffer"/>, an execution state, or
        /// <see cref="MergeTable"/> (egglog's warning, mod.rs:676-689).
        /// </remarks>
        /// <param name="table">The table id.</param>
        /// <returns>The table.</returns>
        /// <exception cref="KeyNotFoundException">No such table, or it is taken out by a merge or rebuild in progress.</exception>
        public Table GetTable(TableId table) => GetTableInfo(table).Table;

        /// <summary>
        /// Gets the table with id <paramref name="table"/> and its metadata. egglog's <c>get_table_info</c>.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <returns>The entry.</returns>
        /// <exception cref="KeyNotFoundException">No such table, or it is taken out by a merge or rebuild in progress.</exception>
        public TableInfo GetTableInfo(TableId table)
        {
            if (tables.TryGetValue(table, out var info))
                return info;

            throw MissingTable(table);
        }

        /// <summary>
        /// Gets the table with id <paramref name="table"/> and its metadata, if present and not taken out.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <param name="info">The entry.</param>
        /// <returns><c>true</c> if found.</returns>
        public bool TryGetTableInfo(TableId table, [MaybeNullWhen(false)] out TableInfo info) => tables.TryGetValue(table, out info);

        /// <summary>
        /// Creates a buffer for staging writes to <paramref name="table"/> and notifies the table, so that the next
        /// <see cref="MergeAll"/> merges it. egglog's <c>new_buffer</c> (mod.rs:708-716). The caller must
        /// <see cref="MutationBuffer.Flush"/> the buffer (what dropping it does in egglog) before that merge.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <returns>The buffer.</returns>
        /// <exception cref="KeyNotFoundException">No such table.</exception>
        public MutationBuffer NewBuffer(TableId table)
        {
            // egglog notifies before looking the table up; looking it up first keeps a bad id from leaving a
            // notification that the next merge could not resolve.
            var buffer = GetTable(table).NewBuffer();
            catalog.Notifications.Notify(table);
            return buffer;
        }

        /// <summary>
        /// Adds a counter starting at zero. egglog's <c>add_counter</c>. Requires exclusive access.
        /// </summary>
        /// <returns>The counter's id.</returns>
        public CounterId AddCounter() => counters.Add();

        /// <summary>
        /// Increments <paramref name="counter"/> and returns its previous value. egglog's <c>inc_counter</c>.
        /// Thread-safe.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value before the increment.</returns>
        public long IncrementCounter(CounterId counter) => counters.Increment(counter);

        /// <summary>
        /// Gets the current value of <paramref name="counter"/>. egglog's <c>read_counter</c>. Thread-safe.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value.</returns>
        public long ReadCounter(CounterId counter) => counters.Read(counter);

        /// <summary>
        /// Registers an external function. egglog's <c>add_external_function</c>; freed ids are reused. Requires
        /// exclusive access.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>Its id.</returns>
        public ExternalFunctionId AddExternalFunction(ExternalFunction function) => externalFunctions.Add(function);

        /// <summary>
        /// Unregisters an external function; its id may be handed out again, so it must not be used afterwards.
        /// egglog's <c>free_external_function</c>. Requires exclusive access.
        /// </summary>
        /// <param name="function">The function's id.</param>
        public void FreeExternalFunction(ExternalFunctionId function) => externalFunctions.Remove(function, out _);

        /// <summary>
        /// Gets the external function with id <paramref name="function"/>.
        /// </summary>
        /// <param name="function">The function's id.</param>
        /// <returns>The function.</returns>
        /// <exception cref="KeyNotFoundException">No such function.</exception>
        public ExternalFunction GetExternalFunction(ExternalFunctionId function) => externalFunctions[function];

        /// <summary>
        /// Estimates the number of rows of <paramref name="table"/>, or of its rows matching
        /// <paramref name="constraint"/>. egglog's <c>estimate_size</c> (mod.rs:463-482): the live row count without a
        /// constraint; the size of the fast subset when the table answers the constraint quickly, which counts
        /// tombstones and so may overestimate; otherwise an exact count by refining the live rows, O(rows).
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <param name="constraint">The constraint, or <c>null</c>.</param>
        /// <returns>The estimate.</returns>
        public int EstimateSize(TableId table, Constraint? constraint = null)
        {
            var t = GetTable(table);
            if (constraint is not { } c)
                return t.Count;

            if (t.TryFastSubset(c, out var fast))
            {
                var size = fast.Size;
                fast.Dispose();
                return size;
            }

            var refined = t.RefineOne(t.RefineLive(t.All()), c);
            var count = refined.Size;
            refined.Dispose();
            return count;
        }

        /// <summary>
        /// Removes every row and every staged write of <paramref name="table"/>. egglog's <c>clear_table</c>
        /// (mod.rs:777-816): the table clears in bulk and, if it had rows, moves to a new generation; its cached indexes
        /// are reset so the next use rebuilds them. Other tables' staged writes are not merged first. Requires exclusive
        /// access.
        /// </summary>
        /// <param name="table">The table id.</param>
        public void ClearTable(TableId table)
        {
            var info = GetTableInfo(table);
            var previous = info.Table.Count;
            info.Table.Clear();
            info.ResetIndexes();
            totalSizeEstimate -= previous;
        }

        /// <summary>
        /// Creates an execution state over <see cref="ReadOnlyView"/> with no staged writes, for writing to the
        /// database outside rules. The caller flushes it (<see cref="ExecutionState.Flush"/>) before merging.
        /// </summary>
        /// <param name="timestamp">The timestamp the state carries.</param>
        /// <returns>The state.</returns>
        public ExecutionState CreateExecutionState(uint timestamp = 0) => new(view, timestamp);

        /// <summary>
        /// Runs <paramref name="action"/> with a fresh execution state over the database, then flushes the state.
        /// egglog's <c>with_execution_state</c> (mod.rs:446-450). A failure the state reports is recorded in
        /// <see cref="Failure"/>.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="action">The action.</param>
        /// <param name="timestamp">The timestamp the state carries.</param>
        /// <returns>The action's result.</returns>
        public TResult WithExecutionState<TResult>(Func<ExecutionState, TResult> action, uint timestamp = 0)
        {
            ArgumentNullException.ThrowIfNull(action);

            var state = new ExecutionState(view, timestamp);
            var result = action(state);
            state.Flush();
            RecordFailure(state);
            return result;
        }

        /// <summary>
        /// Returns <see cref="Failure"/> and clears it, as the bridge takes its panic message.
        /// </summary>
        /// <returns>The first failure since the last call, or <c>null</c>.</returns>
        public string? TakeFailure() => Interlocked.Exchange(ref failure, null);

        /// <summary>
        /// Creates an independent copy: tables (with their staged writes, as each table's clone keeps them) and cached
        /// indexes deep-copied, counters snapshotted, strata copied, external functions shared (see
        /// <see cref="ExternalFunction"/>), container environments copied (see <see cref="ContainerValues.Clone"/>), and
        /// the same tables notified. egglog's <c>Clone for Database</c>, whose copy
        /// shares the notification list with the original instead. Requires exclusive access. O(database size).
        /// </summary>
        /// <returns>The copy.</returns>
        public Database Clone() => new(this);

        /// <summary>
        /// Records the first failure reported through <paramref name="state"/>. Thread-safe.
        /// </summary>
        /// <param name="state">A state the database created.</param>
        internal void RecordFailure(ExecutionState state)
        {
            var message = state.Failure;
            if (message is not null)
                Interlocked.CompareExchange(ref failure, message, null);
        }

        /// <summary>
        /// Enumerates the ids of the tables in the table map, in ascending order.
        /// </summary>
        internal IEnumerable<TableId> VisibleTableIds()
        {
            for (var i = 0; i < tables.IdCount; i++)
            {
                var id = new TableId(i);
                if (tables.ContainsKey(id))
                    yield return id;
            }
        }

        /// <summary>
        /// Takes <paramref name="table"/> out of the table map, as egglog's <c>tables.take</c> /
        /// <c>unwrap_val</c> do while a table merges or provides a rebuild: execution states over the view cannot see it
        /// until <see cref="PutBack"/>.
        /// </summary>
        TableInfo TakeOut(TableId table)
        {
            if (tables.TryTake(table, out var info))
                return info;

            throw MissingTable(table);
        }

        /// <summary>
        /// Puts a table taken out by <see cref="TakeOut"/> back.
        /// </summary>
        void PutBack(TableId table, TableInfo info)
        {
            var had = tables.Insert(table, info);
            Debug.Assert(had == false, "a table was put back twice");
        }

        /// <summary>
        /// Builds the exception for a table id the table map lacks.
        /// </summary>
        KeyNotFoundException MissingTable(TableId table)
        {
            return catalog.TryGetTable(table, out _)
                ? new KeyNotFoundException($"Table {table} is taken out of the database by a merge or rebuild in progress.")
                : new KeyNotFoundException($"Table {table} has not been declared in this database.");
        }

    }

}
