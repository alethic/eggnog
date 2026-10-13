using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Up to <see cref="Times"/> passes of the steps in sequence; stops after a pass with no change (§6.5.2).
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
            for (var pass = 0; pass < Times; pass++)
            {
                if (SequenceSchedule.ExecuteAll(Steps, executor, scope, depth, inCase, splitsEnabled) == false)
                    break;

                changed = true;
            }

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
