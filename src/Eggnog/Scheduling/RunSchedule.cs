using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// One iteration over the union of rulesets' rules (§6.5.2): skipped on a contradictory scope when the run stops on
    /// contradiction.
    /// </summary>
    internal sealed class RunSchedule : Schedule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rulesets">The rulesets.</param>
        public RunSchedule(Ruleset[] rulesets)
        {
            Rulesets = rulesets;
        }

        /// <summary>
        /// Gets the rulesets.
        /// </summary>
        public Ruleset[] Rulesets { get; }

        /// <inheritdoc />
        internal override bool Execute(ScheduleExecutor executor, Scope scope, int depth, bool inCase, bool splitsEnabled)
        {
            if ((inCase || executor.Options.StopOnContradiction) && scope.Engine.IsContradictory)
            {
                if (ReferenceEquals(scope, executor.Top))
                    executor.Statistics.StopReason = RunStopReason.Contradiction;

                return false;
            }

            return executor.RunIteration(scope, executor.RulesFor(Rulesets, scope), inCase);
        }

        /// <inheritdoc />
        internal override void Collect(ISet<Ruleset> rulesets)
        {
            foreach (var ruleset in Rulesets)
                rulesets.Add(ruleset);
        }

    }

}
