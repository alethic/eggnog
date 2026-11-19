using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The steps in order; changed when any step changed.
    /// </summary>
    internal sealed class SequenceSchedule : Schedule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="steps">The steps.</param>
        public SequenceSchedule(Schedule[] steps)
        {
            Steps = steps;
        }

        /// <summary>
        /// Gets the steps.
        /// </summary>
        public Schedule[] Steps { get; }

        /// <inheritdoc />
        internal override bool Execute(ScheduleExecutor executor, Scope scope) => ExecuteAll(Steps, executor, scope);

        /// <inheritdoc />
        internal override void CollectSchedulerSteps(ICollection<RunWithSchedule> steps)
        {
            foreach (var step in Steps)
                step.CollectSchedulerSteps(steps);
        }

        /// <inheritdoc />
        internal override void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions)
        {
            foreach (var step in Steps)
                step.Collect(rulesets, conditions);
        }

        /// <summary>
        /// Executes steps in order and returns whether any changed the scope: one pass of a sequence.
        /// </summary>
        /// <param name="steps">The steps.</param>
        /// <param name="executor">The run's executor.</param>
        /// <param name="scope">The scope.</param>
        /// <returns>Whether any step changed the scope.</returns>
        internal static bool ExecuteAll(Schedule[] steps, ScheduleExecutor executor, Scope scope)
        {
            var changed = false;
            foreach (var step in steps)
                changed |= step.Execute(executor, scope);

            return changed;
        }

    }

}
