using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// What a run has done so far, accumulated by its <see cref="ScheduleExecutor"/>.
    /// </summary>
    internal sealed class RunStatistics
    {

        /// <summary>
        /// The engine iterations made.
        /// </summary>
        public int Iterations;

        /// <summary>
        /// Whether an iteration, or a split step's lifts, changed the top-level scope.
        /// </summary>
        public bool TopChanged;

        /// <summary>
        /// Whether the last iteration or split step in the top-level scope made progress: the iteration changed it, or the
        /// split step opened cases or its lifts changed it.
        /// </summary>
        public bool LastTopStepChanged;

        /// <summary>
        /// Whether a scheduler step in the top-level scope held work back: it changed nothing, and its scheduler could not
        /// stop (<see cref="IRuleScheduler.CanStop"/>).
        /// </summary>
        public bool HeldBack;

        /// <summary>
        /// The stop reason a run step recorded: <see cref="RunStopReason.Completed"/> unless a run step met a
        /// contradiction.
        /// </summary>
        public RunStopReason StopReason;

        /// <summary>
        /// The statistics of every rule that ran, each updated in place through a reference to its value.
        /// </summary>
        public Dictionary<Rule, RuleStatisticsBuilder> Rules = new();

        /// <summary>
        /// The engine's search-and-apply time.
        /// </summary>
        public TimeSpan SearchTime;

        /// <summary>
        /// The engine's merge time.
        /// </summary>
        public TimeSpan MergeTime;

        /// <summary>
        /// The engine's rebuild time.
        /// </summary>
        public TimeSpan RebuildTime;

        /// <summary>
        /// The steps of the run, with <see cref="RunOptions.RecordSteps"/>; <c>null</c> otherwise.
        /// </summary>
        public List<RunStep>? Steps;

        /// <summary>
        /// The number of steps made, recorded or not: the next step's <see cref="RunStep.Index"/>.
        /// </summary>
        public int StepCount;

        /// <summary>
        /// The cases split steps opened, in every scope of the run.
        /// </summary>
        public int CasesOpened;

        /// <summary>
        /// The split requests found and not taken: over the case limit, covered by a live split, or an empty element set;
        /// splits below the depth limit are never looked for.
        /// </summary>
        public int SplitsSkipped;

        /// <summary>
        /// The lifts split steps ran.
        /// </summary>
        public int Lifts;

        /// <summary>
        /// The candidate rows those lifts skipped over their candidate limit.
        /// </summary>
        public long LiftRowsSkipped;

    }

}
