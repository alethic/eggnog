using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Finds the hash-cons entry whose container equals a given one: the key comparison of egglog's
    /// <c>to_id.get(container)</c> and <c>to_id.entry(container)</c>. Compares the stored hash before the contents.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal readonly struct ContainerContentMatch<TContainer> : IEntryMatch<ContainerEntry<TContainer>>
        where TContainer : struct, IContainerValue<TContainer>
    {

        readonly TContainer container;
        readonly ulong hash;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="container">The container looked up.</param>
        /// <param name="hash">Its content hash.</param>
        public ContainerContentMatch(in TContainer container, ulong hash)
        {
            this.container = container;
            this.hash = hash;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in ContainerEntry<TContainer> entry) => entry.Hash == hash && entry.Container.Equals(container);

    }

}
