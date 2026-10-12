using System;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// What one rule did during a run, or during one step of it (D41). Immutable.
    /// </summary>
    public sealed class RuleStatistics
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rule">The rule.</param>
        /// <param name="matches">The engine's match count.</param>
        /// <param name="searchTime">The engine's search-and-apply time for the rule's variants.</param>
        /// <param name="iterationsWithMatches">The iterations in which the rule matched.</param>
        /// <param name="plan">The rule's query plan as the engine reports it; <c>null</c> unless
        /// <see cref="EGraphOptions.RecordPlans"/> is set.</param>
        internal RuleStatistics(Rule rule, long matches, TimeSpan searchTime, int iterationsWithMatches, string? plan)
        {
            Rule = rule;
            Matches = matches;
            SearchTime = searchTime;
            IterationsWithMatches = iterationsWithMatches;
            Plan = plan;
        }

        /// <summary>
        /// Gets the rule.
        /// </summary>
        public Rule Rule { get; }

        /// <summary>
        /// Gets the engine's match count, summed over the run (decision 6: it may differ from egglog's).
        /// </summary>
        public long Matches { get; }

        /// <summary>
        /// Gets the engine's search-and-apply time for the rule's variants, summed over the run.
        /// </summary>
        public TimeSpan SearchTime { get; }

        /// <summary>
        /// Gets the number of iterations in which the rule matched.
        /// </summary>
        public int IterationsWithMatches { get; }

        /// <summary>
        /// Gets the rule's query plan as the engine reports it (the plan of its latest variant that ran); <c>null</c>
        /// unless <see cref="EGraphOptions.RecordPlans"/> is set, or when no variant of the rule ran.
        /// </summary>
        public string? Plan { get; }

        /// <summary>
        /// Renders the statistics: <c>lower uuid: 2 matches in 1 iterations, 0.1 ms</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{Rule.Name}: {Matches} matches in {IterationsWithMatches} iterations, {SearchTime.TotalMilliseconds:0.###} ms");

    }

}
