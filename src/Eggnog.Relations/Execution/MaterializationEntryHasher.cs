using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Returns the hash a <see cref="MaterializationEntry"/> stores, for rehashing a <see cref="Materialization"/>'s
    /// table as it grows.
    /// </summary>
    internal readonly struct MaterializationEntryHasher : IEntryHasher<MaterializationEntry>
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Hash(in MaterializationEntry entry) => entry.Hash;

    }

}
