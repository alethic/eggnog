using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recomputes the hash of a <see cref="ColumnIndexEntry"/> when its table grows: the Fx hash of the value, one
    /// multiply, cheaper than storing the 64-bit hash in the entry.
    /// </summary>
    internal readonly struct ColumnIndexEntryHasher : IEntryHasher<ColumnIndexEntry>
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Hash(in ColumnIndexEntry entry) => FxHasher.Hash(entry.Key);

    }

}
