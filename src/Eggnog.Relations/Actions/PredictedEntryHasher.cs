using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recovers a <see cref="PredictedEntry"/>'s hash from the entry itself when the index grows.
    /// </summary>
    internal readonly struct PredictedEntryHasher : IEntryHasher<PredictedEntry>
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Hash(in PredictedEntry entry) => entry.Hash;

    }

}
