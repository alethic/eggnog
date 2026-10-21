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
