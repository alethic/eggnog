using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The limits of a lift. Immutable once built.
    /// </summary>
    internal sealed class LiftOptions
    {

        readonly int roundLimit = 16;
        readonly int candidateLimit = 64;

        /// <summary>
        /// Gets the default limits.
        /// </summary>
        public static LiftOptions Default { get; } = new();

        /// <summary>
        /// Gets the most rounds one lift runs: a backstop, since rounds stop on their own once the parent stops changing.
        /// Default 16; at least 1.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
        public int RoundLimit
        {
            get => roundLimit;
            init
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
                roundLimit = value;
            }
        }

        /// <summary>
        /// Gets the most parent keys one source row of the seed case may stand for (the product of the member lists of
        /// its id key cells); a row over the limit is skipped and its keys counted in <see cref="LiftReport.Skipped"/>.
        /// Default 64; at least 1.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
        public int CandidateLimit
        {
            get => candidateLimit;
            init
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
                candidateLimit = value;
            }
        }

    }

}
