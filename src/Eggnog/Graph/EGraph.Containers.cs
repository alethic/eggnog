namespace Eggnog
{

    public sealed partial class EGraph
    {

        /// <summary>
        /// Gets the graph's environment of the container type <typeparamref name="TContainer"/>, registering it on first
        /// use as one that holds no e-class ids: an engine definition, so the call takes the definition lock and, while
        /// the root is writable, the root's domain, and throws when another thread is using the writable root. Called
        /// when a container column type is registered, which keeps the environment.
        /// </summary>
        /// <remarks>
        /// Registered on first use, not with the graph, because a database with no container type skips the container
        /// rebuild of every pass. Once the root is frozen, children may run on other threads while this registers; they
        /// read the container set only to look for containers that hold ids, which this is not. A container type that
        /// holds ids must not register so: children of a frozen root read such types while they rebuild.
        /// </remarks>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <returns>The environment.</returns>
        /// <exception cref="System.InvalidOperationException">Another thread is using the writable root.</exception>
        internal Relations.ContainerEnv<TContainer> ValueContainers<TContainer>()
            where TContainer : struct, Relations.IContainerValue<TContainer>
        {
            lock (DefinitionLock)
            {
                var guarded = Root.IsFrozen == false;
                if (guarded)
                    Root.Guard.Enter(Root);

                try
                {
                    return Engine.RegisterBaseContainerType<TContainer>();
                }
                finally
                {
                    if (guarded)
                        Root.Guard.Exit();
                }
            }
        }

    }

}
