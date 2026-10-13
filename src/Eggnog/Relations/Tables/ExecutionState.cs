using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The context that merge functions, rebuilds and rule actions run in: egglog's <c>ExecutionState</c>
    /// (core-relations/src/action/mod.rs:360-596). This part holds what the table layer needs: staging writes into
    /// other tables through a <see cref="TableCatalog"/>, the <see cref="Changed"/> flag, a captured
    /// <see cref="Timestamp"/>, and the stop and failure signals shared by all clones. Further partial files add
    /// the database view and its counters, predicted values, the action instructions (bindings and external
    /// functions), base values, containers and the context-layer state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writes are staged into one <see cref="MutationBuffer"/> per table, created on first use. They reach the table's
    /// pending queues only when <see cref="Flush"/> runs (the explicit form of Rust dropping the state's buffers), and
    /// become visible at the table's next merge.
    /// </para>
    /// <para>
    /// Not thread-safe. Parallel work clones the state per task (<see cref="Clone"/>): a clone stages into fresh
    /// handles of the same tables, starts with <see cref="Changed"/> false, and shares the catalog, timestamp and
    /// signals; each task flushes its clone when done.
    /// </para>
    /// </remarks>
    internal sealed partial class ExecutionState
    {

        readonly TableCatalog catalog;
        readonly ExecutionSignals signals;
        readonly DenseIdMap<TableId, MutationBuffer> buffers;

        /// <summary>
        /// Initializes a state over <paramref name="catalog"/> with no staged writes.
        /// </summary>
        /// <param name="catalog">The tables writes may be staged into.</param>
        /// <param name="timestamp">The timestamp of the batch this state executes.</param>
        public ExecutionState(TableCatalog catalog, uint timestamp = 0)
        {
            ArgumentNullException.ThrowIfNull(catalog);

            this.catalog = catalog;
            signals = new ExecutionSignals();
            buffers = new DenseIdMap<TableId, MutationBuffer>();
            Timestamp = timestamp;
        }

        /// <summary>
        /// Initializes a clone of <paramref name="parent"/>: egglog's <c>Clone for ExecutionState</c>.
        /// </summary>
        /// <param name="parent">The state to clone.</param>
        ExecutionState(ExecutionState parent)
        {
            catalog = parent.catalog;
            signals = parent.signals;
            buffers = new DenseIdMap<TableId, MutationBuffer>(parent.buffers.IdCount);
            foreach (var (id, buffer) in parent.buffers)
                buffers.Insert(id, buffer.FreshHandle());

            Timestamp = parent.Timestamp;

            // Colored contexts: the clone reads and guards as its parent does, and never falls back to finding its view
            // through the catalog (declared in Actions/ExecutionState.Database.cs and Layers/ExecutionState.Layers.cs).
            view = parent.view;
            viewResolved = parent.viewResolved;
            layers = parent.layers;
            guard = parent.guard;
            views = parent.views;
        }

        /// <summary>
        /// Gets the tables this state stages writes into.
        /// </summary>
        public TableCatalog Catalog => catalog;

        /// <summary>
        /// Gets or sets whether anything was staged through this state: set by every staged insert or removal and
        /// read by the merge loop, which keeps merging while merge functions stage writes (a union, say). egglog's
        /// <c>changed</c>.
        /// </summary>
        public bool Changed { get; set; }

        /// <summary>
        /// Gets or sets the timestamp captured for this batch: what rows written by this state carry when no
        /// incoming row supplies one (egglog's merge functions read the timestamp counter or the incoming row's
        /// timestamp column).
        /// </summary>
        public uint Timestamp { get; set; }

        /// <summary>
        /// Gets the signals shared with every clone.
        /// </summary>
        public ExecutionSignals Signals => signals;

        /// <summary>
        /// Gets whether early stopping was requested, through this state or any clone. egglog's <c>should_stop</c>.
        /// </summary>
        public bool ShouldStop => signals.ShouldStop;

        /// <summary>
        /// Gets the first failure reported through this state or any clone, or <c>null</c>.
        /// </summary>
        public string? Failure => signals.Failure;

        /// <summary>
        /// Stages an insertion of <paramref name="row"/> into <paramref name="table"/>. egglog's <c>stage_insert</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="row">The row; copied.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void StageInsert(TableId table, ReadOnlySpan<uint> row)
        {
            GetBuffer(table).StageInsert(row);
            catalog.Notifications.Notify(table);
            Changed = true;
        }

        /// <summary>
        /// Stages an insertion of the row starting at <paramref name="source"/>[<paramref name="offset"/>]. The array
        /// form for compiled plans.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="source">The array holding the row.</param>
        /// <param name="offset">The index of the row's first cell.</param>
        public void StageInsert(TableId table, uint[] source, int offset)
        {
            GetBuffer(table).StageInsert(source, offset);
            catalog.Notifications.Notify(table);
            Changed = true;
        }

        /// <summary>
        /// Stages the removal of the row with key <paramref name="key"/> from <paramref name="table"/>. egglog's
        /// <c>stage_remove</c>.
        /// </summary>
        /// <remarks>
        /// In a store the removal first passes the state's retraction guard (<see cref="RetractionGuard"/>), which
        /// refuses the retractions the context may not make, decided when the removal is staged: at a store's root
        /// without live descendants, one volatile read of the root's gate; in a bare database, one null test.
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <param name="key">The key; copied.</param>
        /// <exception cref="InvalidOperationException">The retraction guard refuses the removal.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void StageRemove(TableId table, ReadOnlySpan<uint> key)
        {
            guard?.CheckRemove(table);
            GetBuffer(table).StageRemove(key);
            catalog.Notifications.Notify(table);
            Changed = true;
        }

        /// <summary>
        /// Stages the removal of the key starting at <paramref name="source"/>[<paramref name="offset"/>]. The array
        /// form for compiled plans.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="source">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <exception cref="InvalidOperationException">The retraction guard refuses the removal.</exception>
        public void StageRemove(TableId table, uint[] source, int offset)
        {
            guard?.CheckRemove(table);
            GetBuffer(table).StageRemove(source, offset);
            catalog.Notifications.Notify(table);
            Changed = true;
        }

        /// <summary>
        /// Gets this state's mutation buffer for <paramref name="table"/>, creating it on first use. egglog's
        /// <c>lazy_init</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The buffer.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MutationBuffer GetBuffer(TableId table)
        {
            if (buffers.TryGetValue(table, out var buffer))
                return buffer;

            return CreateBuffer(table);
        }

        /// <summary>
        /// Creates a state for a parallel task: fresh buffers for the same tables, <see cref="Changed"/> false, the
        /// same catalog, timestamp and signals. egglog's <c>clone</c>.
        /// </summary>
        /// <returns>The clone.</returns>
        public ExecutionState Clone() => new(this);

        /// <summary>
        /// Publishes every staged write to the tables' pending queues: what dropping the state's buffers does in
        /// egglog. The buffers stay usable.
        /// </summary>
        public void Flush()
        {
            foreach (var (_, buffer) in buffers)
                buffer.Flush();
        }

        /// <summary>
        /// Requests early stopping of rule execution, for this state and every clone. egglog's
        /// <c>trigger_early_stop</c>.
        /// </summary>
        public void TriggerEarlyStop() => signals.TriggerEarlyStop();

        /// <summary>
        /// Records a failure (the first one wins) and requests early stopping: the side channel through which merge
        /// functions report what egglog reports with a panic function.
        /// </summary>
        /// <param name="message">What went wrong.</param>
        public void ReportFailure(string message) => signals.ReportFailure(message);

        /// <summary>
        /// Creates and records this state's buffer for <paramref name="table"/>, resolving the table through
        /// <see cref="TableCatalog.GetForStaging"/>: the catalog's table, or in a child context the layer its resolver
        /// supplies. One volatile load more than a plain lookup, per (state, table).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        MutationBuffer CreateBuffer(TableId table)
        {
            var buffer = catalog.GetForStaging(table).NewBuffer();
            buffers.Insert(table, buffer);
            return buffer;
        }

    }

}
