using System.Collections.Generic;

namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// One launch of one build's benchmark app in a campaign: the reports it exported, by class.
    /// </summary>
    /// <param name="Build">The build's name: the name of the directory that holds its runs.</param>
    /// <param name="Round">The round the run belongs to, from its directory's name (<c>r1-a</c> is round 1).</param>
    /// <param name="Name">The run's directory name.</param>
    /// <param name="Reports">The reports, by class name without the namespace.</param>
    internal sealed record CampaignRun(string Build, int Round, string Name, IReadOnlyDictionary<string, BenchmarkReport> Reports);

}
