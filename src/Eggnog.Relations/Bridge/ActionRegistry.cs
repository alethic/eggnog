using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The live registry of action handles that typed primitives use: function name to <see cref="TableAction"/>, plus
    /// the shared <see cref="UnionAction"/> and the default panic function. egglog-bridge's <c>ActionRegistry</c>
    /// (egglog-bridge/src/lib.rs:44-96), which the e-graph shares with the frontend's state wrappers through an
    /// <c>Arc&lt;RwLock&lt;_&gt;&gt;</c>; here the instance itself is the shared handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The e-graph registers each function as it is added (<see cref="EGraph.AddTable"/>); registering a name again
    /// replaces its handle, as egglog's map insert does.
    /// </para>
    /// <para>
    /// Threading: lookups are lock-free and may run on any number of threads, also while a function is being
    /// registered. Registration copies the map and publishes the copy (O(functions), once per declared function), so a
    /// reader always sees a complete map.
    /// </para>
    /// </remarks>
    internal sealed class ActionRegistry
    {

        readonly object sync = new();
        Dictionary<string, TableAction> tableActions;

        /// <summary>
        /// Initializes an empty registry. egglog's <c>ActionRegistry::new</c>.
        /// </summary>
        /// <param name="unionAction">The handle for the e-graph's union-find.</param>
        /// <param name="defaultPanicId">The e-graph's default panic function.</param>
        public ActionRegistry(UnionAction unionAction, ExternalFunctionId defaultPanicId)
        {
            UnionAction = unionAction;
            DefaultPanicId = defaultPanicId;
            tableActions = new Dictionary<string, TableAction>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Gets the handle for the e-graph's union-find. egglog's <c>union_action</c>.
        /// </summary>
        public UnionAction UnionAction { get; }

        /// <summary>
        /// Gets the default panic function ("primitive panicked"), which the frontend's <c>panic</c> action calls. egglog's
        /// <c>default_panic_id</c>.
        /// </summary>
        public ExternalFunctionId DefaultPanicId { get; }

        /// <summary>
        /// Gets the number of registered functions.
        /// </summary>
        public int Count => Volatile.Read(ref tableActions).Count;

        /// <summary>
        /// Registers <paramref name="action"/> under <paramref name="name"/>, replacing any handle of that name. egglog's
        /// <c>register_table</c>.
        /// </summary>
        /// <param name="name">The function's name.</param>
        /// <param name="action">The handle.</param>
        public void RegisterTable(string name, TableAction action)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(action);
            lock (sync)
            {
                var copy = new Dictionary<string, TableAction>(tableActions, StringComparer.Ordinal);
                copy[name] = action;
                Volatile.Write(ref tableActions, copy);
            }
        }

        /// <summary>
        /// Gets the handle registered under <paramref name="name"/>. egglog's <c>lookup_table</c>. Lock-free.
        /// </summary>
        /// <param name="name">The function's name.</param>
        /// <param name="action">The handle.</param>
        /// <returns><c>false</c> if no function of that name is registered.</returns>
        public bool TryGetTable(string name, [MaybeNullWhen(false)] out TableAction action)
        {
            ArgumentNullException.ThrowIfNull(name);
            return Volatile.Read(ref tableActions).TryGetValue(name, out action);
        }

        /// <summary>
        /// Lists every registered function with the current number of rows of its table, as
        /// <paramref name="state"/> sees it. egglog's <c>table_sizes</c>. Order unspecified.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <returns>The names and row counts.</returns>
        public List<KeyValuePair<string, int>> TableSizes(ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            var actions = Volatile.Read(ref tableActions);
            var sizes = new List<KeyValuePair<string, int>>(actions.Count);
            foreach (var (name, action) in actions)
                sizes.Add(new KeyValuePair<string, int>(name, action.RowCount(state)));

            return sizes;
        }

    }

}
