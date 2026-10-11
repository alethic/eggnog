using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The hash a hash-cons entry is stored under: its container's content hash, kept in the entry so growth never
    /// rehashes contents (egglog's <c>|(c, _)| hash_container(c)</c> recomputes it, mod.rs:650).
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal readonly struct ContainerContentHasher<TContainer> : IEntryHasher<ContainerEntry<TContainer>>
        where TContainer : struct, IContainerValue<TContainer>
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Hash(in ContainerEntry<TContainer> entry) => entry.Hash;

    }

}
