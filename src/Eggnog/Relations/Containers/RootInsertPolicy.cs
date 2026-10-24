using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The root's way of putting a rebuilt container back (<see cref="IContainerInsertPolicy{TContainer}"/>): egglog's
    /// own. Every rename happens in place, and a reinsert is
    /// <see cref="ContainerEnv{TContainer}.InsertOwned(in TContainer, ulong, uint, ExecutionState)"/>, egglog's
    /// <c>insert_owned</c> (core-relations/src/containers/mod.rs:411-439). An empty struct: the rebuild loops
    /// specialized for it are the loops without a policy.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal readonly struct RootInsertPolicy<TContainer> : IContainerInsertPolicy<TContainer>
        where TContainer : struct, IContainerValue<TContainer>
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool RenamesInPlace(uint id) => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Insert(ContainerEnv<TContainer> env, in TContainer container, ulong hash, uint id, ExecutionState state) =>
            env.InsertOwned(container, hash, id, state);

    }

}
