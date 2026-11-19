using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// One iteration over the union of rulesets' rules: skipped on a contradictory scope when the run stops on
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
        internal override bool Execute(ScheduleExecutor executor, Scope scope)
        {
            // The scope is brought up to date first, as the iteration would bring it anyway, so this costs nothing
            // more, and nothing at all when the run does not stop on contradiction.
            if (executor.Options.StopOnContradiction && IsContradictory(scope.Engine))
            {
                executor.Statistics.StopReason = RunStopReason.Contradiction;
                return false;
            }

            return executor.RunIteration(scope, ScheduleExecutor.RulesFor(Rulesets, scope));
        }

        /// <summary>
        /// Gets whether a scope is contradictory once brought up to date (a no-op when it is current or stopped).
        /// </summary>
        internal static bool IsContradictory(EngineScope engine)
        {
            engine.EnsureClean();
            return engine.IsStopped || engine.IsContradictory;
        }

        /// <inheritdoc />
        internal override void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions)
        {
            foreach (var ruleset in Rulesets)
                rulesets.Add(ruleset);
        }

    }

}
