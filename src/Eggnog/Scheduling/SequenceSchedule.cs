using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The steps in order; changed when any step changed (§6.5.2).
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
        internal override bool Execute(ScheduleExecutor executor, Scope scope, int depth, bool inCase, bool splitsEnabled) =>
            ExecuteAll(Steps, executor, scope, depth, inCase, splitsEnabled);

        /// <inheritdoc />
        internal override void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions, ISet<SplitRule> splits)
        {
            foreach (var step in Steps)
                step.Collect(rulesets, conditions, splits);
        }

        /// <summary>
        /// Executes steps in order and returns whether any changed the scope: one pass of a sequence.
        /// </summary>
        /// <param name="steps">The steps.</param>
        /// <param name="executor">The run's executor.</param>
        /// <param name="scope">The scope.</param>
        /// <param name="depth">The split depth.</param>
        /// <param name="inCase">Whether the scope is a case.</param>
        /// <param name="splitsEnabled">Whether split steps run.</param>
        /// <returns>Whether any step changed the scope.</returns>
        internal static bool ExecuteAll(Schedule[] steps, ScheduleExecutor executor, Scope scope, int depth, bool inCase, bool splitsEnabled)
        {
            var changed = false;
            foreach (var step in steps)
                changed |= step.Execute(executor, scope, depth, inCase, splitsEnabled);

            return changed;
        }

    }

}
