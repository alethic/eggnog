using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Rehashes a <see cref="TermDagEntry"/> when the hash-cons index grows: the entry carries its hash.
    /// </summary>
    internal readonly struct TermDagEntryHasher : IEntryHasher<TermDagEntry>
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Hash(in TermDagEntry entry) => entry.Hash;

    }

}
