using System;

namespace Eggnog
{

    /// <summary>
    /// Accumulates one rule's <see cref="RuleStatistics"/> over a run. A mutable struct, held in place as a value of
    /// <see cref="RunStatistics.Rules"/> and updated through a reference to it, so a run allocates no object per rule
    /// until its report is built; <c>default</c> is a rule that has not matched.
    /// </summary>
    internal struct RuleStatisticsBuilder
    {

        long matches;
        TimeSpan searchTime;
        int iterationsWithMatches;
        string? plan;

        /// <summary>
        /// Adds one iteration's figures for the rule.
        /// </summary>
        /// <param name="matches">The engine's match count in the iteration.</param>
        /// <param name="searchTime">The engine's search-and-apply time in the iteration.</param>
        /// <param name="plan">The rule's plan in the iteration, when recorded; the latest one recorded is kept.</param>
        public void Add(long matches, TimeSpan searchTime, string? plan)
        {
            this.matches += matches;
            this.searchTime += searchTime;
            if (matches > 0)
                iterationsWithMatches++;

            if (plan is not null)
                this.plan = plan;
        }

        /// <summary>
        /// Builds the statistics.
        /// </summary>
        /// <param name="rule">The rule.</param>
        /// <returns>The statistics.</returns>
        public readonly RuleStatistics Build(Rule rule) => new(rule, matches, searchTime, iterationsWithMatches, plan);

    }

}
