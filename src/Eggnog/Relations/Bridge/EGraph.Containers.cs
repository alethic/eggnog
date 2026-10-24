using System;

namespace Eggnog.Relations
{

    internal sealed partial class EGraph
    {

        /// <summary>
        /// Gets the base value registry: egglog's <c>base_values</c> and <c>base_values_mut</c> (lib.rs:214-262). Register
        /// a type before declaring columns of it (<see cref="ColumnTy.Base"/>); external functions reach the same registry
        /// through their execution state.
        /// </summary>
        public BaseValues BaseValues => db.BaseValues;

        /// <summary>
        /// Gets the container environments: egglog's <c>container_values</c> and <c>container_values_mut</c>
        /// (lib.rs:220-229).
        /// </summary>
        internal ContainerValues ContainerValues => db.Containers;

        /// <summary>
        /// Registers the container type <typeparamref name="TContainer"/>, whose fresh ids come from the e-graph's id
        /// counter and whose ids, when two containers become equal, are unioned in the union-find keeping the smaller
        /// (<see cref="UnionContainerMerge"/>, stamped with the current timestamp). egglog's <c>register_container_ty</c>
        /// (lib.rs:238-257). Registering a type again does nothing.
        /// </summary>
        /// <remarks>
        /// <paramref name="holdsIds"/> says whether the containers hold e-class ids. Such eq-sort containers cannot be
        /// interned in a child context (interning one there throws <see cref="InvalidOperationException"/>); a child
        /// rebuilds, in its container layers, those its own displacements reach (<see cref="ContainerLayers"/>).
        /// Containers over base values register with <c>holdsIds: false</c> and work in every context.
        /// </remarks>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="holdsIds">Whether the containers hold e-class ids.</param>
        /// <returns>The type's id.</returns>
        internal ContainerTypeId RegisterContainerType<TContainer>(bool holdsIds = true)
            where TContainer : struct, IContainerValue<TContainer>
        {
            return db.Containers.RegisterType<TContainer>(idCounter, containerMerge, holdsIds);
        }

        /// <summary>
        /// Registers the container type <typeparamref name="TContainer"/> as one holding ids if it is not registered,
        /// and leaves one registered over base values as it is.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        internal void EnsureContainerType<TContainer>()
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (db.Containers.TryGetTypeId<TContainer>(out _) == false)
                RegisterContainerType<TContainer>();
        }

        /// <summary>
        /// Registers the container type <typeparamref name="TContainer"/> as one that holds no e-class ids, or gets it,
        /// and returns its environment, whose <see cref="ContainerEnv{TContainer}.GetOrInsertShared"/> interns without
        /// an execution state from <see cref="Counters"/>. Registration is thread-safe
        /// (<see cref="ContainerValues.RegisterType{TContainer}"/>), so threads that race here get one environment, but
        /// it must not run while the containers rebuild, which needs them to itself
        /// (<see cref="ContainerValues.RebuildAll"/>): so the caller holds the root's domain while the root is writable,
        /// the only time they rebuild. The library's callers also hold their graph's definition lock, which keeps the
        /// library's own cache of environments in step with the engine's.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <returns>The environment.</returns>
        internal ContainerEnv<TContainer> RegisterBaseContainerType<TContainer>()
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (db.Containers.TryGetEnvironment<TContainer>(out var existing))
                return existing;

            RegisterContainerType<TContainer>(holdsIds: false);
            return db.Containers.GetEnvironment<TContainer>();
        }

        /// <summary>
        /// Registers the container type <typeparamref name="TContainer"/> as one that may hold e-class ids, or makes the
        /// environment of one registered over base values hold them, and returns its environment: egglog keeps a
        /// container type's eq-sort and base containers in one environment, apart by <c>do_rebuild</c>. Its
        /// <see cref="ContainerEnv{TContainer}.GetOrInsertAtRoot"/> interns a container of classes from the root, and
        /// <see cref="ContainerEnv{TContainer}.GetOrInsertShared"/> still interns one of values anywhere. The caller holds
        /// the e-graph's definition lock and the root's domain while it is writable.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <returns>The environment.</returns>
        internal ContainerEnv<TContainer> RegisterEqContainerType<TContainer>()
            where TContainer : struct, IContainerValue<TContainer>
        {
            RegisterContainerType<TContainer>(holdsIds: true);
            return db.Containers.GetEnvironment<TContainer>();
        }

        /// <summary>
        /// Gets the store's counters, which every execution state of the store mints ids from.
        /// </summary>
        internal CounterSet Counters => db.Counters;

        /// <summary>
        /// Interns <paramref name="container"/> at the root, registering its type first if needed (as one holding ids), and
        /// returns its id: a fresh id the first time its contents are seen. egglog's <c>get_container_value</c>
        /// (lib.rs:231-236). Interning is immediate and global; a child interns through
        /// <see cref="EGraphContext.GetContainerValue{TContainer}"/>. The contents are not canonicalized.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="container">The container; it must not change afterwards.</param>
        /// <returns>The container's id.</returns>
        internal Value GetContainerValue<TContainer>(in TContainer container)
            where TContainer : struct, IContainerValue<TContainer>
        {
            EnsureContainerType<TContainer>();
            var state = db.CreateEngineState(NextTimestamp);
            var id = db.Containers.RegisterValue(container, state);
            state.Flush();
            db.RecordFailure(state);
            return new Value(id);
        }

        /// <summary>
        /// Runs <paramref name="action"/> with a fresh execution state over the e-graph's database, stamped with the
        /// current timestamp, and flushes what it staged; the writes land at the next <see cref="FlushUpdates"/>. egglog's
        /// <c>with_execution_state</c> (lib.rs:928-944): a raw escape for one-shot manipulation from outside any rule,
        /// with <see cref="TableAction"/>s and <see cref="UnionAction"/>s, say. Rows and unions the root staged earlier
        /// through the <see cref="EGraphContext"/> API are flushed first, so writes land in the order the host issued
        /// them (see <see cref="EGraphContext.WithExecutionStateAtRoot{TResult}"/>).
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="action">The action.</param>
        /// <returns>The action's result.</returns>
        /// <exception cref="InvalidOperationException">The root's domain is held by another thread.</exception>
        internal TResult WithExecutionState<TResult>(Func<ExecutionState, TResult> action) => root.WithExecutionStateAtRoot(action);

    }

}
