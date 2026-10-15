using System.Collections.Generic;

namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// One case's medians in one round: one value per run of each build.
    /// </summary>
    /// <param name="Round">The round.</param>
    /// <param name="Baseline">The baseline build's medians, in nanoseconds, one per run.</param>
    /// <param name="Candidate">The candidate build's medians, in nanoseconds, one per run.</param>
    internal sealed record RoundSamples(int Round, IReadOnlyList<double> Baseline, IReadOnlyList<double> Candidate);

}
