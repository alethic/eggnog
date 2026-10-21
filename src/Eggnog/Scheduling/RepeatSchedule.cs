using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Up to <see cref="Times"/> passes of the steps in sequence; stops after a pass with no change in which no scheduler
    /// step held work back (egglog's <c>can_stop</c>: <see cref="Schedule.RunWith"/>).
    /// </summary>
    internal sealed class RepeatSchedule : Schedule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="times">The most passes.</param>
        /// <param name="steps">The steps of a pass.</param>
        public RepeatSchedule(int times, Schedule[] steps)
        {
            Times = times;
            Steps = steps;
        }

        /// <summary>
        /// Gets the most passes.
        /// </summary>
        public int Times { get; }

        /// <summary>
        /// Gets the steps of a pass.
        /// </summary>
        public Schedule[] Steps { get; }

        /// <inheritdoc />
        internal override bool Execute(ScheduleExecutor executor, Scope scope, int depth, bool inCase, bool splitsEnabled)
        {
            var changed = false;
            var outer = executor.HeldBack;
            try
            {
                for (var pass = 0; pass < Times; pass++)
                {
                    executor.HeldBack = false;
                    if (SequenceSchedule.ExecuteAll(Steps, executor, scope, depth, inCase, splitsEnabled))
                        changed = true;
                    else if (executor.HeldBack == false)
                        break;

                    outer |= executor.HeldBack;
                }
            }
            finally
            {
                executor.HeldBack = outer;
            }

            return changed;
        }

        /// <inheritdoc />
        internal override void CollectSchedulerSteps(ICollection<RunWithSchedule> steps)
        {
            foreach (var step in Steps)
                step.CollectSchedulerSteps(steps);
        }

        /// <inheritdoc />
        internal override void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions, ISet<SplitRule> splits, ISet<Ruleset> inCases,
            bool inCase)
        {
            foreach (var step in Steps)
                step.Collect(rulesets, conditions, splits, inCases, inCase);
        }

    }

}
