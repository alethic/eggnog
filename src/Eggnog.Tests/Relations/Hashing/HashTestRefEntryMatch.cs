namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Matches a <see cref="HashTestRefEntry"/> by key.
    /// </summary>
    internal readonly struct HashTestRefEntryMatch : IEntryMatch<HashTestRefEntry>
    {

        readonly string key;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="key">The key to match.</param>
        public HashTestRefEntryMatch(string key)
        {
            this.key = key;
        }

        /// <inheritdoc />
        public bool Matches(in HashTestRefEntry entry) => entry.Key == key;

    }

}
