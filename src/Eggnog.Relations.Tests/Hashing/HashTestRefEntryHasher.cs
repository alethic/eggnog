namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Rehashes a <see cref="HashTestRefEntry"/> from its stored hash.
    /// </summary>
    internal readonly struct HashTestRefEntryHasher : IEntryHasher<HashTestRefEntry>
    {

        /// <inheritdoc />
        public ulong Hash(in HashTestRefEntry entry) => entry.Hash;

    }

}
