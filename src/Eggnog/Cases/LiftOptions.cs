using System;

namespace Eggnog
{

    /// <summary>
    /// The limits of a lift (§6.9, colors §11.2): of <see cref="CaseSet.Lift"/>, and of the lifts a split step makes
    /// (<see cref="RunOptions.Lift"/>). Immutable. A lift cut short by a limit is sound, with fewer facts; it leaves no memo,
    /// so a later lift with larger limits runs in full.
    /// </summary>
    public sealed class LiftOptions
    {

        readonly int roundLimit = 16;
        readonly int candidateLimit = 64;
        Relations.LiftOptions? engine;

        /// <summary>
        /// Gets the default limits: 16 rounds, 64 candidates.
        /// </summary>
        public static LiftOptions Default { get; } = new();

        /// <summary>
        /// Gets the most rounds one lift makes: a backstop, since rounds stop on their own once the parent stops changing
        /// (term lift costs one round per nesting level). Default 16; at least 1.
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
        /// Gets the most parent-level candidates one case row may stand for (the product of the member lists of its class
        /// columns); a row over the limit is skipped and counted (<see cref="LiftReport.Skipped"/>,
        /// <see cref="RunReport.LiftRowsSkipped"/>). Default 64; at least 1.
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

        /// <summary>
        /// Gets the engine's options with these limits, made once per instance (a race makes equal copies, harmlessly).
        /// </summary>
        internal Relations.LiftOptions Engine =>
            engine ??= new Relations.LiftOptions { RoundLimit = roundLimit, CandidateLimit = candidateLimit };

    }

}
