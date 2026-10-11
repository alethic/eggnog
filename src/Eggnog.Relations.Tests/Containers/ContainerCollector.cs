using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Collects the containers and ids an environment visits.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    readonly struct ContainerCollector<TContainer> : IContainerVisitor<TContainer>
        where TContainer : struct, IContainerValue<TContainer>
    {

        /// <summary>
        /// Initializes a collector with an empty list.
        /// </summary>
        public ContainerCollector()
        {
            Items = new List<(TContainer Container, uint Id)>();
        }

        /// <summary>
        /// Gets the visited containers and ids, in visit order.
        /// </summary>
        public List<(TContainer Container, uint Id)> Items { get; }

        /// <inheritdoc />
        public void Visit(in TContainer container, uint id) => Items.Add((container, id));

    }

}
