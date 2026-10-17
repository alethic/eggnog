namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// What the campaign's rules make of one case (<see cref="ComparisonRules"/>).
    /// </summary>
    internal enum CaseOutcome
    {

        /// <summary>
        /// Gated: at least two rounds left, and the ratio within the gate in every one of them. A round is dropped when
        /// its same-build runs disagree or its spread is unknown (<see cref="CaseComparer.Agrees"/>). The candidate
        /// becomes the baseline for this case.
        /// </summary>
        Pass,

        /// <summary>
        /// Gated: at least two rounds left, and the ratio above the gate in every one of them. A confirmed failure
        /// becomes a roadmap item naming the case.
        /// </summary>
        Failure,

        /// <summary>
        /// Gated, but neither a pass nor a failure, in fewer than four rounds: the rounds left fall on both sides of the
        /// gate, or fewer than two are left because rounds were dropped for disagreeing runs. Add a round.
        /// </summary>
        Inconclusive,

        /// <summary>
        /// Neither a pass nor a failure after the most rounds, whatever was dropped: report it with its spread, leave it
        /// ungated, and re-run it in the next window.
        /// </summary>
        Ungated,

        /// <summary>
        /// Shorter than the gate's minimum time: the ratio is reported, not gated.
        /// </summary>
        Reported,

        /// <summary>
        /// Fewer than two rounds left in fewer than four rounds, with fewer than two rounds run or a build with fewer
        /// than two runs in a round: run more.
        /// </summary>
        NeedsRuns,

        /// <summary>
        /// Fewer than two rounds left in fewer than four rounds, and a run of either build has no median for the case:
        /// it failed, or its report has no <c>Median</c> column (run with <c>--allStats</c>).
        /// </summary>
        NoMedian,

    }

}
