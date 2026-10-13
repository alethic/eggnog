using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Passes of the steps in sequence until one changes nothing.
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
            while (SequenceSchedule.ExecuteAll(Steps, executor, scope, depth, inCase, splitsEnabled))
                changed = true;

            return changed;
        }

        /// <inheritdoc />
        internal override void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions, ISet<SplitRule> splits)
        {
            foreach (var step in Steps)
                step.Collect(rulesets, conditions, splits);
        }

    }

}
