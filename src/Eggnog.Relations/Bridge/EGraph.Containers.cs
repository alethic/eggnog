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
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <returns>The type's id.</returns>
        internal ContainerTypeId RegisterContainerType<TContainer>()
            where TContainer : struct, IContainerValue<TContainer>
        {
            return db.Containers.RegisterType<TContainer>(idCounter, containerMerge);
        }

        /// <summary>
        /// Interns <paramref name="container"/>, registering its type first if needed, and returns its id: a fresh id the
        /// first time its contents are seen. egglog's <c>get_container_value</c> (lib.rs:231-236). Interning is immediate
        /// and global (map §2.10). The contents are not canonicalized.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="container">The container; it must not change afterwards.</param>
        /// <returns>The container's id.</returns>
        internal Value GetContainerValue<TContainer>(in TContainer container)
            where TContainer : struct, IContainerValue<TContainer>
        {
            RegisterContainerType<TContainer>();
            var state = db.CreateExecutionState(NextTimestamp);
            var id = db.Containers.RegisterValue(container, state);
            state.Flush();
            db.RecordFailure(state);
            return new Value(id);
        }

        /// <summary>
        /// Runs <paramref name="action"/> with a fresh execution state over the e-graph's database, stamped with the
        /// current timestamp, and flushes what it staged; the writes land at the next <see cref="FlushUpdates"/>. egglog's
        /// <c>with_execution_state</c> (lib.rs:928-944): a raw escape for one-shot manipulation from outside any rule,
        /// with <see cref="TableAction"/>s and <see cref="UnionAction"/>s, say.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="action">The action.</param>
        /// <returns>The action's result.</returns>
        internal TResult WithExecutionState<TResult>(Func<ExecutionState, TResult> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return db.WithExecutionState(action, NextTimestamp);
        }

    }

}
