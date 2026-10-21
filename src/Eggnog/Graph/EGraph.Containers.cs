using System.Collections.Concurrent;

namespace Eggnog
{

    public sealed partial class EGraph
    {

        /// <summary>
        /// The environments of the container types of values registered so far, by container type.
        /// </summary>
        readonly ConcurrentDictionary<System.Type, Relations.ContainerEnvBase> valueContainers = new();

        /// <summary>
        /// Gets the graph's environment of the container type <typeparamref name="TContainer"/>, registering it on first
        /// use as one that holds no e-class ids. Lock-free once registered. The first call is an engine definition: it
        /// takes the definition lock and, while the root is writable, the root's domain, and throws when another thread
        /// is using the writable root. Called when a container column type is registered, which keeps the environment.
        /// </summary>
        /// <remarks>
        /// Registered on first use, not with the graph, so that a graph holds the environments of the container types
        /// it uses only (the built-in sets' it registers with itself); the container rebuild of each pass walks only the
        /// types that hold ids. Once the root is frozen, children may run on other threads while this registers; they
        /// read the container set only to look for containers that hold ids, which this is not. A container type that
        /// holds ids must not register so: children of a frozen root read such types while they rebuild.
        /// </remarks>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <returns>The environment.</returns>
        /// <exception cref="System.InvalidOperationException">The type's first registration runs while another thread is
        /// using the writable root.</exception>
        internal Relations.ContainerEnv<TContainer> ValueContainers<TContainer>()
            where TContainer : struct, Relations.IContainerValue<TContainer>
        {
            if (valueContainers.TryGetValue(typeof(TContainer), out var known))
                return (Relations.ContainerEnv<TContainer>)known;

            lock (DefinitionLock)
            {
                if (valueContainers.TryGetValue(typeof(TContainer), out known))
                    return (Relations.ContainerEnv<TContainer>)known;

                var guarded = Root.IsFrozen == false;
                if (guarded)
                    Root.Guard.Enter(Root);

                try
                {
                    var env = Engine.RegisterBaseContainerType<TContainer>();
                    valueContainers[typeof(TContainer)] = env;
                    return env;
                }
                finally
                {
                    if (guarded)
                        Root.Guard.Exit();
                }
            }
        }

        /// <summary>
        /// Gets the graph's environment of the container type <typeparamref name="TContainer"/> as one that holds e-class
        /// ids, registering it so, or making the environment of the type's containers of values hold them: egglog keeps a
        /// container type's eq-sort and base containers in one environment, apart by <c>do_rebuild</c>. An engine
        /// definition the first time: it takes the definition lock and the writable root's domain.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <returns>The environment.</returns>
        /// <exception cref="System.InvalidOperationException">The root is frozen, whose children read the container
        /// types that hold ids while they rebuild; or another thread is using the root.</exception>
        internal Relations.ContainerEnv<TContainer> ClassContainers<TContainer>()
            where TContainer : struct, Relations.IContainerValue<TContainer>
        {
            if (valueContainers.TryGetValue(typeof(TContainer), out var known) && known.HoldsIds)
                return (Relations.ContainerEnv<TContainer>)known;

            lock (DefinitionLock)
            {
                if (valueContainers.TryGetValue(typeof(TContainer), out known) && known.HoldsIds)
                    return (Relations.ContainerEnv<TContainer>)known;

                if (Root.IsFrozen)
                    throw new System.InvalidOperationException(
                        $"A container type of classes registers while the root is writable; the root is frozen, and its children read such types while they rebuild.");

                Root.Guard.Enter(Root);
                try
                {
                    var env = Engine.RegisterEqContainerType<TContainer>();
                    valueContainers[typeof(TContainer)] = env;
                    return env;
                }
                finally
                {
                    Root.Guard.Exit();
                }
            }
        }

    }

}
