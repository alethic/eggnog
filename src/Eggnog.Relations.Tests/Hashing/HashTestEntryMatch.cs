namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Matches a <see cref="HashTestEntry"/> by key.
    /// </summary>
    internal struct HashTestEntryMatch : IEntryMatch<HashTestEntry>
    {

        readonly int key;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="key">The key to match.</param>
        public HashTestEntryMatch(int key)
        {
            this.key = key;
        }

        /// <summary>
        /// Gets how many entries this match has been asked about.
        /// </summary>
        public int Calls { get; private set; }

        /// <inheritdoc />
        public bool Matches(in HashTestEntry entry)
        {
            Calls++;
            return entry.Key == key;
        }

    }

}
