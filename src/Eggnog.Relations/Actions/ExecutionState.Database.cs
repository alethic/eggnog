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
        /// <param name="view">The database view.</param>
        /// <param name="timestamp">The timestamp of the batch this state executes.</param>
        public ExecutionState(DatabaseView view, uint timestamp = 0)
            : this(view?.Catalog ?? throw new ArgumentNullException(nameof(view)), timestamp)
        {
            this.view = view;
            viewResolved = true;
        }

        /// <summary>
        /// Gets the view of the database this state runs against, or <c>null</c> for a state over a bare
        /// <see cref="TableCatalog"/>. Clones share their parent's view. egglog's <c>db</c> field.
        /// </summary>
        /// <remarks>
        /// The clone constructor (Tables/ExecutionState.cs) does not copy the view, so a clone looks it up from its
        /// catalog once (<see cref="DatabaseView.TryGetForCatalog"/>) and caches it.
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
        /// Increments <paramref name="counter"/> and returns its previous value. egglog's <c>inc_counter</c>.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value before the increment.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        public long IncrementCounter(CounterId counter) => RequireView().IncrementCounter(counter);

        /// <summary>
        /// Gets the current value of <paramref name="counter"/>. egglog's <c>read_counter</c>.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        public long ReadCounter(CounterId counter) => RequireView().ReadCounter(counter);

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
        /// Calls the external function <paramref name="function"/> with this state on the arguments
        /// <paramref name="args"/>[<paramref name="offset"/>..+<paramref name="count"/>]: the array form of
        /// <see cref="TryCallExternalFunction(ExternalFunctionId, ReadOnlySpan{uint}, out uint)"/> for compiled plans,
        /// which cannot take a span. One registry lookup and one virtual call, as the span form.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="args">The array holding the arguments.</param>
        /// <param name="offset">The index of the first argument.</param>
        /// <param name="count">The number of arguments.</param>
        /// <param name="result">The value produced, when the result is <c>true</c>.</param>
        /// <returns><c>false</c> if the function produced no value.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        /// <exception cref="KeyNotFoundException">No such function.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The range lies outside <paramref name="args"/>.</exception>
        public bool TryCallExternalFunction(ExternalFunctionId function, uint[] args, int offset, int count, out uint result)
        {
            return RequireView().GetExternalFunction(function).TryInvoke(this, args, offset, count, out result);
        }

        /// <summary>
        /// Gets the table with id <paramref name="table"/> as the database view sees it: egglog's <c>get_table</c>,
        /// which fails for tables the database has taken out while merging or rebuilding. Reading a table while rules
        /// run can break seminaive evaluation.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <returns>The table.</returns>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        /// <exception cref="KeyNotFoundException">No such table, or it is taken out.</exception>
        public Table GetTable(TableId table) => RequireView().GetTable(table);

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
