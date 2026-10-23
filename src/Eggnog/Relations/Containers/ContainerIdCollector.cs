using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Collects every reverse-map entry under one container id (<see cref="ContainerEnv{TContainer}.CollectById"/>): it
    /// matches none, so the probe goes on to the end of the id's probe sequence, and appends each entry with the id on
    /// the way. Two containers share an id only after a rebuild renamed one container's id to another's.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal readonly struct ContainerIdCollector<TContainer> : IEntryMatch<ContainerEntry<TContainer>>
        where TContainer : struct, IContainerValue<TContainer>
    {

        readonly uint id;
        readonly List<ContainerEntry<TContainer>> destination;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id">The id collected.</param>
        /// <param name="destination">Receives the entries.</param>
        public ContainerIdCollector(uint id, List<ContainerEntry<TContainer>> destination)
        {
            this.id = id;
            this.destination = destination;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in ContainerEntry<TContainer> entry)
        {
            if (entry.Id == id)
                destination.Add(entry);

            return false;
        }

    }

}
