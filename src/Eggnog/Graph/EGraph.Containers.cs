using System.Threading;

namespace Eggnog
{

    public sealed partial class EGraph
    {

        Relations.ContainerEnv<Relations.PairContainer>? pairs;

        /// <summary>
        /// Gets the graph's pair environment, registering it on first use as one that holds no e-class ids: an engine
        /// definition, so the first call takes the definition lock and, while the root is writable, the root's domain,
        /// and throws when another thread is using the writable root. Lock-free once registered.
        /// </summary>
        /// <remarks>
        /// Registered on first use, not with the graph, because a database with no container type skips the container
        /// rebuild of every pass. Once the root is frozen, children may run on other threads while this registers; they
        /// read the container set only to look for containers that hold ids, which this is not. A container type that
        /// holds ids must not register so: children of a frozen root read such types while they rebuild.
        /// </remarks>
        /// <returns>The environment.</returns>
        /// <exception cref="System.InvalidOperationException">Another thread is using the writable root.</exception>
        internal Relations.ContainerEnv<Relations.PairContainer> PairContainers()
        {
            if (Volatile.Read(ref pairs) is { } registered)
                return registered;

            lock (DefinitionLock)
            {
                if (pairs is { } raced)
                    return raced;

                var guarded = Root.IsFrozen == false;
                if (guarded)
                    Root.Guard.Enter(Root);

                try
                {
                    var env = Engine.RegisterBaseContainerType<Relations.PairContainer>();
                    Volatile.Write(ref pairs, env);
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
