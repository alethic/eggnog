using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// One iteration over the union of rulesets' rules.
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
        internal override bool Execute(ScheduleExecutor executor, Scope scope) =>
            executor.RunIteration(scope, ScheduleExecutor.RulesFor(Rulesets, scope));

        /// <inheritdoc />
        internal override void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions)
        {
            foreach (var ruleset in Rulesets)
                rulesets.Add(ruleset);
        }

    }

}
