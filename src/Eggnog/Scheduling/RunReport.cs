using System;
using System.Collections.Generic;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// What a run did: its iterations and changes, why it ended, and each rule's statistics. Immutable.
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
        /// <param name="steps">Every step of the run, with <see cref="RunOptions.RecordSteps"/>; otherwise empty.</param>
        /// <param name="casesOpened">The cases split steps opened.</param>
        /// <param name="splitsSkipped">The split requests not taken.</param>
        /// <param name="lifts">The lifts split steps ran.</param>
        /// <param name="liftRowsSkipped">The candidate rows those lifts skipped.</param>
        internal RunReport(int iterations, bool changed, bool saturated, RunStopReason stopReason, TimeSpan elapsed,
            IReadOnlyDictionary<Rule, RuleStatistics> rules, TimeSpan searchTime, TimeSpan mergeTime, TimeSpan rebuildTime,
            IReadOnlyList<RunStep> steps, int casesOpened = 0, int splitsSkipped = 0, int lifts = 0, long liftRowsSkipped = 0)
        {
            Steps = steps;
            CasesOpened = casesOpened;
            SplitsSkipped = splitsSkipped;
            Lifts = lifts;
            LiftRowsSkipped = liftRowsSkipped;
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
        /// Gets the number of engine iterations the run made, in every scope of the run, cases included; mirror syncs and
        /// split matchers do not count.
        /// </summary>
        public int Iterations { get; }

        /// <summary>
        /// Gets whether an iteration, split step or lift of the run changed the scope (the engine's change flags; for a
        /// split step, whether its lifts moved the scope's <see cref="Scope.Version"/>: cases opened and dropped change
        /// nothing there).
        /// </summary>
        public bool Changed { get; }

        /// <summary>
        /// Gets whether the run completed and its last iteration or split step in the scope made no progress: the iteration
        /// changed nothing, or the split step opened no case and its lifts changed nothing.
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
        /// Gets every rule that ran, with its statistics. Match counts are the engine's and may differ from egglog's.
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
        /// Gets every step of the run, in order, when <see cref="RunOptions.RecordSteps"/> is set; otherwise empty.
        /// </summary>
        public IReadOnlyList<RunStep> Steps { get; }

        /// <summary>
        /// Gets the cases the run's split steps opened, in every scope of the run.
        /// </summary>
        public int CasesOpened { get; }

        /// <summary>
        /// Gets the split requests the run's split steps found and did not take: over <see cref="RunOptions.CaseLimit"/>,
        /// covered by a live split of the same rule at the same position in the scope or an ancestor (counted at each
        /// execution of the step that finds it covered), or an empty element set. Sound: fewer facts. A split below
        /// <see cref="RunOptions.SplitDepthLimit"/> is never looked for, so it is not counted.
        /// </summary>
        public int SplitsSkipped { get; }

        /// <summary>
        /// Gets the lifts the run's split steps ran, including those answered at once because nothing they read had
        /// changed since the last full lift.
        /// </summary>
        public int Lifts { get; }

        /// <summary>
        /// Gets the parent-level candidate rows those lifts did not examine: each case row that stood for more than
        /// <see cref="LiftOptions.CandidateLimit"/> parent keys adds the number of keys it stood for, so this counts
        /// candidates, not skipped case rows.
        /// </summary>
        public long LiftRowsSkipped { get; }

        /// <summary>
        /// Renders the report: <c>Completed after 5 iterations, saturated, changed, 3 rules, 1.2 ms</c>, with
        /// <c>, 2 cases</c> after the rules when split steps opened cases.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{StopReason} after {Iterations} iterations{(Saturated ? ", saturated" : "")}{(Changed ? ", changed" : "")}, {Rules.Count} rules{(CasesOpened > 0 ? $", {CasesOpened} cases" : "")}, {Elapsed.TotalMilliseconds:0.###} ms");

    }

}
