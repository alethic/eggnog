using System.Collections.Generic;

namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// One case's comparison of a candidate build with the baseline over the campaign's rounds.
    /// </summary>
    /// <param name="Key">The case (<see cref="BenchmarkReport.CaseKey"/>).</param>
    /// <param name="Outcome">What the campaign's rules make of it.</param>
    /// <param name="Rounds">Each round's ratio and spread, in round order.</param>
    /// <param name="Baseline">The median of every baseline run's median, in nanoseconds, over the rounds in which every
    /// run of both builds has a median; <see cref="double.NaN"/> when no round has.</param>
    /// <param name="Candidate">The median of every candidate run's median, in nanoseconds, over the same rounds.</param>
    internal sealed record CaseComparison(string Key, CaseOutcome Outcome, IReadOnlyList<RoundResult> Rounds, double Baseline, double Candidate);

}
