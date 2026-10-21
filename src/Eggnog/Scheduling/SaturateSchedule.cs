using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Passes of the steps in sequence until one changes nothing and no scheduler step of it holds work back: egglog's
    /// <c>saturate</c>, which stops on a pass that updated nothing, and egglog-experimental's, which stops on
    /// <c>can_stop</c>; the two agree except over scheduler steps, which only egglog-experimental's has
    /// (<see cref="Schedule.RunWith"/>).
    /// </summary>
    internal sealed class SaturateSchedule : Schedule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="steps">The steps of a pass.</param>
        public SaturateSchedule(Schedule[] steps)
        {
            Steps = steps;
        }

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
                while (true)
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
