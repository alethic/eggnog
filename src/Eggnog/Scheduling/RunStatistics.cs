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
        /// Whether an iteration changed the top-level scope.
        /// </summary>
        public bool TopChanged;

        /// <summary>
        /// Whether the last iteration in the top-level scope changed it.
        /// </summary>
        public bool LastTopStepChanged;

        /// <summary>
        /// The stop reason a run step recorded: <see cref="RunStopReason.Completed"/> unless a run step met a
        /// contradiction.
        /// </summary>
        public RunStopReason StopReason;

        /// <summary>
        /// The statistics of every rule that ran.
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

    }

}
