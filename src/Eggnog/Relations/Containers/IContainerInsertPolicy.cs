namespace Eggnog.Relations
{

    /// <summary>
    /// How a container rebuild puts a rebuilt container back: the step that egglog's <c>insert_owned</c>
    /// (core-relations/src/containers/mod.rs:411-439) takes at the root, and that a child context's container pass
    /// takes over its view (<see cref="ContainerLayers"/>). The rebuild loops of <see cref="ContainerEnv{TContainer}"/>
    /// and <see cref="ContainerRebuildPredicate{TContainer, TPolicy}"/> are generic over it, so each policy gets loops
    /// of its own: the root's (<see cref="RootInsertPolicy{TContainer}"/>) compile to the code without a policy, and a
    /// child's (<see cref="LayeredInsertPolicy{TContainer}"/>) asks its layers.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal interface IContainerInsertPolicy<TContainer>
        where TContainer : struct, IContainerValue<TContainer>
    {

        /// <summary>
        /// Gets whether a full rebuild may rename, in place, an entry whose id alone changed to <paramref name="id"/>
        /// (<see cref="ContainerRebuildPredicate{TContainer, TPolicy}"/>); otherwise the entry is taken out and put
        /// back through <see cref="Insert"/>, as an entry whose contents changed is.
        /// </summary>
        /// <param name="id">The entry's new id.</param>
        /// <returns><c>true</c> to rename the entry in place.</returns>
        bool RenamesInPlace(uint id);

        /// <summary>
        /// Puts <paramref name="container"/>, whose content hash is <paramref name="hash"/>, back into
        /// <paramref name="env"/> under <paramref name="id"/>, merging it with an equal container if there is one, and
        /// returns the id it ends up with: <paramref name="id"/>, or the merge's result.
        /// </summary>
        /// <param name="env">The environment being rebuilt.</param>
        /// <param name="container">The rebuilt container, out of every structure of <paramref name="env"/>.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="id">The id to put it under: its old id, rebuilt.</param>
        /// <param name="state">The state the merge function runs in.</param>
        /// <returns>The id the container ends up with.</returns>
        uint Insert(ContainerEnv<TContainer> env, in TContainer container, ulong hash, uint id, ExecutionState state);

    }

}
