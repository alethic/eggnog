using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Wraps a failure of one seed of a property test, so the seed is in the message.
    /// </summary>
    sealed class SeedFailure : Exception
    {

        /// <summary>
        /// Initializes a failure of <paramref name="seed"/> caused by <paramref name="inner"/>.
        /// </summary>
        public SeedFailure(int seed, Exception inner)
            : base($"Seed {seed} failed: {inner.Message}", inner)
        {
            Seed = seed;
        }

        /// <summary>
        /// Gets the failing seed.
        /// </summary>
        public int Seed { get; }

    }

}
