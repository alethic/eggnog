using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The hash a <see cref="ContainerValueIndex"/> slot is stored under: the Fx hash of its element value.
    /// </summary>
    internal readonly struct ValueIndexEntryHasher : IEntryHasher<ValueIndexEntry>
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Hash(in ValueIndexEntry entry) => FxHasher.Hash(entry.Element);

    }

}
