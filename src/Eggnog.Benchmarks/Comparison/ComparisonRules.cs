using System;

namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// The campaign's thresholds: a case is gated when its baseline median is at least <see cref="MinimumNanoseconds"/>.
    /// A round whose same-build runs differ by more than <see cref="Spread"/> is dropped. A gated case passes when at
    /// least <see cref="MinimumRounds"/> rounds remain and every one's ratio is at most <see cref="Gate"/>, fails when at
    /// least that many remain and every one's ratio is above it, and is otherwise inconclusive, to be run again up to
    /// <see cref="MaximumRounds"/> rounds in all.
    /// </summary>
    /// <param name="Gate">The largest candidate-to-baseline ratio of medians that passes: 1.02.</param>
    /// <param name="Spread">The largest relative spread between same-build runs of a round at which they agree and the
    /// round counts: 0.01, that is 1%.</param>
    /// <param name="MinimumNanoseconds">The shortest baseline median that is gated: 100 µs. Shorter cases are
    /// reported, not gated.</param>
    /// <param name="MinimumRounds">The rounds a case needs before it is judged, and the rounds whose runs agree that a
    /// pass or a failure needs: 2.</param>
    /// <param name="MaximumRounds">The rounds after which an inconclusive case is reported with its spread and left
    /// ungated: 4.</param>
    internal sealed record ComparisonRules(double Gate, double Spread, double MinimumNanoseconds, int MinimumRounds, int MaximumRounds)
    {

        /// <summary>
        /// Gets the campaign's rules: a 2% gate on cases of 100 µs or more, rounds whose same-build runs are more than 1%
        /// apart dropped, two rounds left on one side of the gate to decide, and up to four rounds for an inconclusive
        /// case.
        /// </summary>
        public static ComparisonRules Default { get; } = new(1.02, 0.01, 100_000, 2, 4);

        /// <summary>
        /// Checks that the rules are usable.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The gate is not above zero, the spread or the minimum time is
        /// negative, fewer than one round is needed, or the most rounds are fewer than the rounds needed.</exception>
        public void Validate()
        {
            if (double.IsFinite(Gate) == false || Gate <= 0)
                throw new ArgumentOutOfRangeException(nameof(Gate), Gate, "The gate must be a ratio above zero.");

            if (double.IsFinite(Spread) == false || Spread < 0)
                throw new ArgumentOutOfRangeException(nameof(Spread), Spread, "The spread must be at least zero.");

            if (double.IsFinite(MinimumNanoseconds) == false || MinimumNanoseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(MinimumNanoseconds), MinimumNanoseconds, "The minimum time must be at least zero.");

            if (MinimumRounds < 1)
                throw new ArgumentOutOfRangeException(nameof(MinimumRounds), MinimumRounds, "At least one round is needed.");

            if (MaximumRounds < MinimumRounds)
                throw new ArgumentOutOfRangeException(nameof(MaximumRounds), MaximumRounds, "The most rounds must be at least the rounds needed.");
        }

    }

}
