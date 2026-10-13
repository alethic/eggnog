using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Returns the hash a <see cref="TableEntry"/> stores, so key indexes grow without touching the row buffer:
    /// egglog's <c>TableEntry::hashcode</c> passed as hashbrown's hasher.
    /// </summary>
    internal readonly struct TableEntryHasher : IEntryHasher<TableEntry>
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Hash(in TableEntry entry) => entry.Hash;

    }

}
