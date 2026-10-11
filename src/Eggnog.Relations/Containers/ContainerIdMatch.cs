using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Finds a reverse-map entry by container id: the lookup of egglog's <c>to_container.get(&amp;value)</c>. When two
    /// containers share an id (possible only after a rebuild renames one container's id to another's), the first in
    /// probe order matches.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal readonly struct ContainerIdMatch<TContainer> : IEntryMatch<ContainerEntry<TContainer>>
        where TContainer : struct, IContainerValue<TContainer>
    {

        readonly uint id;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id">The id looked up.</param>
        public ContainerIdMatch(uint id)
        {
            this.id = id;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in ContainerEntry<TContainer> entry) => entry.Id == id;

    }

}
