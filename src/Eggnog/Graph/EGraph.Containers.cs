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
        /// types that hold ids. Once the root is frozen, children may run on other threads while this registers: the
        /// engine publishes its container set copy-on-write
        /// (<see cref="Relations.ContainerValues.RegisterType{TContainer}"/>), so a child that looks environments up or
        /// enumerates them sees the new type whole or not at all; and this type holds no ids, so a child's container
        /// pass, which walks only the types that do, passes it by. A container type that holds ids registers through
        /// <see cref="ClassContainers{TContainer}"/>, which may also run while children of a frozen root run.
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
        /// container type's eq-sort and base containers in one environment, apart by <c>do_rebuild</c>. Lock-free once
        /// it holds ids. The first such call is an engine definition: it takes the definition lock and, while the root is
        /// writable, the root's domain, and throws when another thread is using the writable root.
        /// </summary>
        /// <remarks>
        /// It registers while the root is frozen too, as egglog declares a container sort in any command, inside a
        /// <c>(push)</c> as well (tests/container-rebuild.egg): that changes a definition, the set of container types
        /// and whether one holds ids, not the root's state, so, as <see cref="ValueContainers{TContainer}"/>, it takes no
        /// domain of a frozen root, whose children may run on other threads meanwhile. The engine publishes the new
        /// environment copy-on-write, and the flag of one that comes to hold ids with a volatile write after giving it the
        /// root's container log (<see cref="Relations.ContainerValues.RegisterType{TContainer}"/>), so a child's
        /// container pass and catch-up, which look the types up on each pass, see the type whole, as one that holds ids
        /// or not, and its layers make the type's layer on first use whatever the type's id. An environment registered
        /// or made to hold ids while the root is frozen holds no container of classes itself, since a frozen root interns
        /// none: its containers so far, interned from any scope, hold values only, and the containers of classes its
        /// children make live in their own container layers.
        /// </remarks>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <returns>The environment.</returns>
        /// <exception cref="System.InvalidOperationException">The type's first registration as one that holds ids runs
        /// while another thread is using the writable root.</exception>
        internal Relations.ContainerEnv<TContainer> ClassContainers<TContainer>()
            where TContainer : struct, Relations.IContainerValue<TContainer>
        {
            if (valueContainers.TryGetValue(typeof(TContainer), out var known) && known.HoldsIds)
                return (Relations.ContainerEnv<TContainer>)known;

            lock (DefinitionLock)
            {
                if (valueContainers.TryGetValue(typeof(TContainer), out known) && known.HoldsIds)
                    return (Relations.ContainerEnv<TContainer>)known;

                // The root freezes under the definition lock, and never thaws, so the test holds while this registers.
                var guarded = Root.IsFrozen == false;
                if (guarded)
                    Root.Guard.Enter(Root);

                try
                {
                    var env = Engine.RegisterEqContainerType<TContainer>();
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

    }

}
