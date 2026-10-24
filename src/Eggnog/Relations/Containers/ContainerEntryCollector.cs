using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Collects every container an environment visits (<see cref="ContainerEnv{TContainer}.ForEach{TVisitor}"/>) as an
    /// entry, its content hash computed again: for a check that probes other environments, which it does after the walk
    /// rather than under the walked environment's shard locks.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal readonly struct ContainerEntryCollector<TContainer> : IContainerVisitor<TContainer>
        where TContainer : struct, IContainerValue<TContainer>
    {

        readonly List<ContainerEntry<TContainer>> destination;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="destination">Receives the entries, in the walk's order.</param>
        public ContainerEntryCollector(List<ContainerEntry<TContainer>> destination)
        {
            this.destination = destination;
        }

        /// <inheritdoc />
        public void Visit(in TContainer container, uint id) => destination.Add(new ContainerEntry<TContainer>(container, container.ComputeHash(), id));

    }

}
