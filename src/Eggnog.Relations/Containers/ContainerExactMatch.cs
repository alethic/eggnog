using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Finds the reverse-map entry for one particular container under one id: id, content hash and contents all equal.
    /// Used to remove exactly the entry that mirrors a hash-cons entry, even when another container shares the id.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal readonly struct ContainerExactMatch<TContainer> : IEntryMatch<ContainerEntry<TContainer>>
        where TContainer : struct, IContainerValue<TContainer>
    {

        readonly TContainer container;
        readonly ulong hash;
        readonly uint id;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="id">Its id.</param>
        public ContainerExactMatch(in TContainer container, ulong hash, uint id)
        {
            this.container = container;
            this.hash = hash;
            this.id = id;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in ContainerEntry<TContainer> entry) => entry.Id == id && entry.Hash == hash && entry.Container.Equals(container);

    }

}
