using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recomputes the hash of an <see cref="IdEntry"/> from its id when a <see cref="RawHashTable{TEntry}"/> grows:
    /// <see cref="FxHasher.Hash(uint)"/> of <see cref="IdEntry.Id"/>, the hash every lookup of the entry uses.
    /// </summary>
    internal readonly struct IdEntryHasher : IEntryHasher<IdEntry>
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Hash(in IdEntry entry) => FxHasher.Hash(entry.Id);

    }

}
