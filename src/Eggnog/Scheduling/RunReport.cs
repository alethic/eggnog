using System;
using System.Collections.Generic;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// What a run did (D41): its iterations and changes, why it ended, and each rule's statistics. Immutable.
    /// </summary>
    public sealed class RunReport
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="iterations">The engine iterations.</param>
        /// <param name="changed">Whether an iteration changed the scope.</param>
        /// <param name="saturated">Whether the run completed and its last iteration changed nothing.</param>
        /// <param name="stopReason">Why the run ended.</param>
        /// <param name="elapsed">The run's wall time.</param>
        /// <param name="rules">Every rule that ran, with its statistics.</param>
        /// <param name="searchTime">The engine's search-and-apply time.</param>
        /// <param name="mergeTime">The engine's merge time.</param>
        /// <param name="rebuildTime">The engine's rebuild time.</param>
        internal RunReport(int iterations, bool changed, bool saturated, RunStopReason stopReason, TimeSpan elapsed,
            IReadOnlyDictionary<Rule, RuleStatistics> rules, TimeSpan searchTime, TimeSpan mergeTime, TimeSpan rebuildTime)
        {
            Iterations = iterations;
            Changed = changed;
            Saturated = saturated;
            StopReason = stopReason;
            Elapsed = elapsed;
            Rules = rules;
            SearchTime = searchTime;
            MergeTime = mergeTime;
            RebuildTime = rebuildTime;
        }

        /// <summary>
        /// Gets the number of engine iterations the run made.
        /// </summary>
        public int Iterations { get; }

        /// <summary>
        /// Gets whether an iteration of the run changed the scope (the engine's change flags, §6.5.1).
        /// </summary>
        public bool Changed { get; }

        /// <summary>
        /// Gets whether the run completed and its last iteration in the scope changed nothing.
        /// </summary>
        public bool Saturated { get; }

        /// <summary>
        /// Gets why the run ended.
        /// </summary>
        public RunStopReason StopReason { get; }

        /// <summary>
        /// Gets the run's wall time.
        /// </summary>
        public TimeSpan Elapsed { get; }

        /// <summary>
        /// Gets every rule that ran, with its statistics. Match counts are the engine's and may differ from egglog's
        /// (decision 6).
        /// </summary>
        public IReadOnlyDictionary<Rule, RuleStatistics> Rules { get; }

        /// <summary>
        /// Gets the engine's search-and-apply time, summed over the run.
        /// </summary>
        public TimeSpan SearchTime { get; }

        /// <summary>
        /// Gets the engine's merge time, summed over the run.
        /// </summary>
        public TimeSpan MergeTime { get; }

        /// <summary>
        /// Gets the engine's rebuild time, summed over the run.
        /// </summary>
        public TimeSpan RebuildTime { get; }

        /// <summary>
        /// Renders the report: <c>Completed after 5 iterations, saturated, changed, 3 rules, 1.2 ms</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{StopReason} after {Iterations} iterations{(Saturated ? ", saturated" : "")}{(Changed ? ", changed" : "")}, {Rules.Count} rules, {Elapsed.TotalMilliseconds:0.###} ms");

    }

}
