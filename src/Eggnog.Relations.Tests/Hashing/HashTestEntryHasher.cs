namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Rehashes a <see cref="HashTestEntry"/> from its stored hash.
    /// </summary>
    internal readonly struct HashTestEntryHasher : IEntryHasher<HashTestEntry>
    {

        /// <inheritdoc />
        public ulong Hash(in HashTestEntry entry) => entry.Hash;

    }

}
