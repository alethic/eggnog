namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// One case's comparison in one round.
    /// </summary>
    /// <param name="Round">The round.</param>
    /// <param name="Baseline">The median of the baseline's run medians, in nanoseconds.</param>
    /// <param name="Candidate">The median of the candidate's run medians, in nanoseconds.</param>
    /// <param name="Ratio">The candidate over the baseline.</param>
    /// <param name="Spread">The larger of the two builds' spreads between their runs: the largest run median over the
    /// smallest, minus one; <see cref="double.NaN"/> when either build has fewer than two runs in the round or a run has no
    /// median.</param>
    internal readonly record struct RoundResult(int Round, double Baseline, double Candidate, double Ratio, double Spread);

}
