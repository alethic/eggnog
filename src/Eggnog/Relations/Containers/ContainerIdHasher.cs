using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The hash a reverse-map entry is stored under: the Fx hash of its id, as egglog's <c>to_container</c> DashMap
    /// hashes its <c>Value</c> keys.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal readonly struct ContainerIdHasher<TContainer> : IEntryHasher<ContainerEntry<TContainer>>
        where TContainer : struct, IContainerValue<TContainer>
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Hash(in ContainerEntry<TContainer> entry) => FxHasher.Hash(entry.Id);

    }

}
