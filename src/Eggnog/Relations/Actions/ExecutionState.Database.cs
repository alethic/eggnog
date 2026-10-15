using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class ExecutionState
    {

        /// <summary>
        /// The database view, once known: set by the view constructor, or recovered from the catalog on first use by
        /// a state made another way (a clone).
        /// </summary>
        DatabaseView? view;

        /// <summary>
        /// Whether <see cref="view"/> was set or looked up.
        /// </summary>
        bool viewResolved;

        /// <summary>
        /// Initializes a state over a database's read-only view, with no staged writes: egglog's
        /// <c>ExecutionState::new(db.read_only_view(), ..)</c>. Writes stage through the database's catalog and notify
        /// its notification list; counters and external functions come from the view.
        /// </summary>
        /// <remarks>
        /// In a store the state also takes the child context's layering, through which it reads committed tables, and
        /// the database's retraction guard (<see cref="Database.GuardForNewState"/>), which its removals go through.
        /// </remarks>
        /// <param name="view">The database view.</param>
        /// <param name="timestamp">The timestamp of the batch this state executes.</param>
        public ExecutionState(DatabaseView view, uint timestamp = 0)
            : this(view?.Catalog ?? throw new ArgumentNullException(nameof(view)), timestamp)
        {
            this.view = view;
            viewResolved = true;
            layers = view.Owner.Layers;
            guard = view.Owner.GuardForNewState();
        }

        /// <summary>
        /// Gets the view of the database this state runs against, or <c>null</c> for a state over a bare
        /// <see cref="TableCatalog"/>. Clones share their parent's view. egglog's <c>db</c> field.
        /// </summary>
        /// <remarks>
        /// The clone constructor (Tables/ExecutionState.cs) copies the view; a state over a bare catalog looks it up from
        /// its catalog once (<see cref="DatabaseView.TryGetForCatalog"/>) and caches it.
        /// </remarks>
        public DatabaseView? View
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (viewResolved == false)
                    ResolveView();

                return view;
            }
        }

        /// <summary>
        /// Increments <paramref name="counter"/> and returns its previous value. egglog's <c>inc_counter</c>. In a child
        /// context, the store's id counter mints from the blocks the child reserves on the shared counter
        /// (<see cref="IdBlockAllocator"/>), so ids minted in different contexts never collide.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value before the increment.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        public long IncrementCounter(CounterId counter) => RequireView().IncrementCounter(counter);

        /// <summary>
        /// Gets the current value of <paramref name="counter"/>. egglog's <c>read_counter</c>, except for the store's
        /// clock, which reads as this state's <see cref="Timestamp"/>: the epoch its context captured.
        /// </summary>
        /// <remarks>
        /// The pin is what stamps every write of a run, pass or batch with the epoch its context captured, even while a
        /// sibling context on another thread advances the clock: <see cref="ReadCounterInstr"/> (egglog's
        /// <c>next_ts</c>), <see cref="UnionContainerMerge"/>'s timestamp counter and the bridge's actions all read the
        /// clock through here. At a root that runs alone the pinned value equals the live one, so its numbering is
        /// egglog's. A database without a store has no clock (its <see cref="DatabaseView.ClockCounterRep"/> is −1),
        /// and every counter reads live. One integer compare per call.
        /// </remarks>
        /// <param name="counter">The counter.</param>
        /// <returns>The value.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        public long ReadCounter(CounterId counter)
        {
            var database = RequireView();
            return counter.Rep == database.ClockCounterRep ? Timestamp : database.ReadCounter(counter);
        }

        /// <summary>
        /// Calls the external function <paramref name="function"/> with this state. egglog's <c>call_external_func</c>.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="args">The arguments.</param>
        /// <param name="result">The value produced, when the result is <c>true</c>.</param>
        /// <returns><c>false</c> if the function produced no value.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        /// <exception cref="KeyNotFoundException">No such function.</exception>
        public bool TryCallExternalFunction(ExternalFunctionId function, ReadOnlySpan<uint> args, out uint result)
        {
            return RequireView().GetExternalFunction(function).TryInvoke(this, args, out result);
        }

        /// <summary>
        /// Gets the table with id <paramref name="table"/> as the database view sees it: egglog's <c>get_table</c>,
        /// which fails for tables the database has taken out while merging or rebuilding. Reading a table while rules
        /// run can break seminaive evaluation.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <returns>The table.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database, or it runs in a child
        /// context, whose tables are layered.</exception>
        /// <exception cref="KeyNotFoundException">No such table, or it is taken out.</exception>
        public Table GetTable(TableId table) => layers is null ? RequireView().GetTable(table) : ThrowLayeredRead(table);

        /// <summary>
        /// Gets the human-readable name of <paramref name="table"/>. egglog's <c>table_name</c>.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <returns>The name, or <c>null</c>.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        public string? GetTableName(TableId table) => RequireView().GetTableName(table);

        /// <summary>
        /// Looks the view up from the catalog, for a state made by the clone constructor.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ResolveView()
        {
            DatabaseView.TryGetForCatalog(catalog, out view);
            viewResolved = true;
        }

        /// <summary>
        /// Gets the view or throws.
        /// </summary>
        DatabaseView RequireView() => View ?? ThrowNoView();

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static DatabaseView ThrowNoView()
        {
            throw new InvalidOperationException("This execution state runs over a bare table catalog, not a database.");
        }

    }

}
