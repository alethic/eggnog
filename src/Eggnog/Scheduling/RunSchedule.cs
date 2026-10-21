using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// One iteration over the union of rulesets' rules: skipped on a contradictory scope when the run stops on
    /// contradiction, which a case always does.
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
            // A case always stops on contradiction, whether a split made it or a host's CaseSet, and so does a run in a case
            // of a split. The scope is brought up to date first: a case's parent may have changed since its last iteration
            // (a lift between a split's phases), and a stopped case reads as contradictory without one. The iteration would
            // bring it up to date anyway, so this costs nothing more, and nothing at all when the run does not stop on
            // contradiction.
            if ((inCase || scope.IsCase || executor.Options.StopOnContradiction) && IsContradictory(scope.Engine))
            {
                if (ReferenceEquals(scope, executor.Top))
                    executor.Statistics.StopReason = RunStopReason.Contradiction;

                return false;
            }

            return executor.RunIteration(scope, ScheduleExecutor.RulesFor(Rulesets, scope), inCase);
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
        internal override void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions, ISet<SplitRule> splits, ISet<Ruleset> inCases,
            bool inCase)
        {
            foreach (var ruleset in Rulesets)
            {
                rulesets.Add(ruleset);
                if (inCase)
                    inCases.Add(ruleset);
            }
        }

    }

}
