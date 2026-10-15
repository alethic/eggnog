using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// Applies the campaign's method to one case: per round, the ratio of the candidate's median to the baseline's
    /// (each the median of that build's run medians in the round) and the spread between same-build runs; then the
    /// outcome under <see cref="ComparisonRules"/>.
    /// </summary>
    internal static class CaseComparer
    {

        /// <summary>
        /// How far above a threshold a ratio or spread may be and still count as at it: the rounding of the division
        /// that computes it, so that a ratio of exactly 1.02 or a spread of exactly 1% meets the rule.
        /// </summary>
        const double Tolerance = 1e-12;

        /// <summary>
        /// Compares one case.
        /// </summary>
        /// <param name="key">The case.</param>
        /// <param name="rounds">The case's medians, one entry per round in which both builds ran it.</param>
        /// <param name="rules">The thresholds.</param>
        /// <returns>The comparison.</returns>
        /// <exception cref="ArgumentException">Two entries name the same round.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The rules are not usable (<see cref="ComparisonRules.Validate"/>).</exception>
        public static CaseComparison Compare(string key, IReadOnlyList<RoundSamples> rounds, ComparisonRules rules)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(rounds);
            ArgumentNullException.ThrowIfNull(rules);
            rules.Validate();

            var ordered = rounds.OrderBy(r => r.Round).ToList();
            for (var i = 1; i < ordered.Count; i++)
                if (ordered[i].Round == ordered[i - 1].Round)
                    throw new ArgumentException($"Round {ordered[i].Round} is given twice for {key}.", nameof(rounds));

            var results = new List<RoundResult>(ordered.Count);
            foreach (var round in ordered)
            {
                var baseline = Median(round.Baseline);
                var candidate = Median(round.Candidate);
                var ratio = baseline > 0 ? candidate / baseline : double.NaN;
                // Math.Max returns NaN when either spread is NaN, so a build with one run leaves the round's spread unknown.
                var spread = Math.Max(Spread(round.Baseline), Spread(round.Candidate));

                results.Add(new RoundResult(round.Round, baseline, candidate, ratio, spread));
            }

            // The overall figures leave out the rounds without a median of both builds, so that one failed run does not
            // hide them.
            var complete = ordered.Where(HasMedians).ToList();
            var allBaseline = Median(complete.SelectMany(r => r.Baseline).ToList());
            var allCandidate = Median(complete.SelectMany(r => r.Candidate).ToList());
            var outcome = Decide(ordered, results, allBaseline, rules);
            return new CaseComparison(key, outcome, results, allBaseline, allCandidate);
        }

        /// <summary>
        /// Gets the median of <paramref name="values"/>: the middle value, or the mean of the two middle values of an
        /// even count. O(n log n).
        /// </summary>
        /// <param name="values">The values.</param>
        /// <returns>The median; <see cref="double.NaN"/> when there are none or any is <see cref="double.NaN"/>.</returns>
        public static double Median(IReadOnlyList<double> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (values.Count == 0 || values.Any(double.IsNaN))
                return double.NaN;

            var sorted = values.OrderBy(v => v).ToArray();
            var middle = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        /// <summary>
        /// Gets the spread between runs of one build: the largest value over the smallest, minus one.
        /// </summary>
        /// <param name="values">The runs' medians.</param>
        /// <returns>The spread; <see cref="double.NaN"/> for fewer than two values, a value that is
        /// <see cref="double.NaN"/>, or a smallest value that is not above zero.</returns>
        public static double Spread(IReadOnlyList<double> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (values.Count < 2 || values.Any(double.IsNaN))
                return double.NaN;

            var min = values.Min();
            return min > 0 ? values.Max() / min - 1 : double.NaN;
        }

        /// <summary>
        /// Gets whether a round counts towards the outcome: its spread is known and at most
        /// <see cref="ComparisonRules.Spread"/>. A round is dropped when its same-build runs disagree, and when its spread
        /// is unknown: a build has fewer than two runs in it, or a run has no median for the case.
        /// </summary>
        /// <param name="round">The round.</param>
        /// <param name="rules">The thresholds.</param>
        /// <returns><c>true</c> when the round counts towards the outcome.</returns>
        public static bool Agrees(RoundResult round, ComparisonRules rules)
        {
            ArgumentNullException.ThrowIfNull(rules);
            return round.Spread <= rules.Spread + Tolerance;
        }

        /// <summary>
        /// Gets whether both builds have runs in a round and every run has a median for the case.
        /// </summary>
        /// <param name="round">The round.</param>
        /// <returns><c>true</c> when the round has a ratio.</returns>
        static bool HasMedians(RoundSamples round) =>
            round.Baseline.Count > 0 && round.Candidate.Count > 0
            && round.Baseline.Any(double.IsNaN) == false && round.Candidate.Any(double.IsNaN) == false;

        /// <summary>
        /// Decides a case's outcome from its rounds. A case whose baseline median is under
        /// <see cref="ComparisonRules.MinimumNanoseconds"/> is reported. Otherwise every round that does not agree
        /// (<see cref="Agrees"/>) is dropped, and the case passes or fails when at least
        /// <see cref="ComparisonRules.MinimumRounds"/> rounds remain and all of them fall on the same side of the gate.
        /// Otherwise it is ungated once it has <see cref="ComparisonRules.MaximumRounds"/> rounds. Before that, too few
        /// rounds left because of a run without a median, too few rounds, or too few runs of a build in a round say what to
        /// run; anything else is inconclusive.
        /// </summary>
        /// <param name="rounds">The samples, in round order.</param>
        /// <param name="results">Each round's ratio and spread, in the same order.</param>
        /// <param name="baseline">The median of the baseline's run medians over the rounds with a median of both builds;
        /// <see cref="double.NaN"/> when there are none.</param>
        /// <param name="rules">The thresholds.</param>
        /// <returns>The outcome.</returns>
        static CaseOutcome Decide(List<RoundSamples> rounds, List<RoundResult> results, double baseline, ComparisonRules rules)
        {
            if (baseline < rules.MinimumNanoseconds)
                return CaseOutcome.Reported;

            var kept = results.Where(r => Agrees(r, rules)).ToList();
            if (kept.Count >= rules.MinimumRounds)
            {
                if (kept.All(r => r.Ratio <= rules.Gate + Tolerance))
                    return CaseOutcome.Pass;

                if (kept.All(r => r.Ratio > rules.Gate + Tolerance))
                    return CaseOutcome.Failure;
            }

            if (results.Count >= rules.MaximumRounds)
                return CaseOutcome.Ungated;

            if (kept.Count < rules.MinimumRounds)
            {
                if (rounds.Any(r => r.Baseline.Any(double.IsNaN) || r.Candidate.Any(double.IsNaN)))
                    return CaseOutcome.NoMedian;

                if (rounds.Count < rules.MinimumRounds || rounds.Any(r => r.Baseline.Count < 2 || r.Candidate.Count < 2))
                    return CaseOutcome.NeedsRuns;
            }

            return CaseOutcome.Inconclusive;
        }

    }

}
