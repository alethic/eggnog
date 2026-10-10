using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Keeps the <see cref="HashTestEntry"/> entries whose key is not in a removal set, adding a delta to the value of
    /// each kept entry to check in-place updates.
    /// </summary>
    internal struct HashTestEntryPredicate : IEntryPredicate<HashTestEntry>
    {

        readonly HashSet<int> remove;
        readonly int delta;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="remove">The keys to remove.</param>
        /// <param name="delta">Added to the value of every kept entry.</param>
        public HashTestEntryPredicate(HashSet<int> remove, int delta)
        {
            this.remove = remove;
            this.delta = delta;
        }

        /// <summary>
        /// Gets how many entries the predicate has seen.
        /// </summary>
        public int Visited { get; private set; }

        /// <inheritdoc />
        public bool Keep(ref HashTestEntry entry)
        {
            Visited++;
            if (remove.Contains(entry.Key))
                return false;

            entry = entry with { Value = entry.Value + delta };
            return true;
        }

    }

}
