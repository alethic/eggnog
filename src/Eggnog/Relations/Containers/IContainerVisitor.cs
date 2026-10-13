namespace Eggnog.Relations
{

    /// <summary>
    /// Receives the containers of one environment with their ids, from <see cref="ContainerEnv{TContainer}.ForEach{TVisitor}"/>
    /// and <see cref="ContainerValues.ForEach{TContainer, TVisitor}"/>: the closure of egglog's
    /// <c>ContainerValues::for_each</c> (core-relations/src/containers/mod.rs:135-143).
    /// </summary>
    /// <remarks>
    /// Implement it on a struct so the walk is specialized for the visitor. The visitor runs while the environment holds
    /// the lock of the shard being walked, so it must not call back into the same environment.
    /// </remarks>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal interface IContainerVisitor<TContainer>
        where TContainer : struct, IContainerValue<TContainer>
    {

        /// <summary>
        /// Receives one container and its id.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="id">Its id.</param>
        void Visit(in TContainer container, uint id);

    }

}
