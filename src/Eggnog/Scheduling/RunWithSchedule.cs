using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// One step of a ruleset under a scheduler (<see cref="Schedule.RunWith"/>): egglog's
    /// <c>step_rules_with_scheduler</c>. Skipped on a contradictory scope when the run stops on contradiction, as a
    /// <see cref="RunSchedule"/> is.
    /// </summary>
    internal sealed class RunWithSchedule : Schedule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="scheduler">The scheduler.</param>
        /// <param name="ruleset">The ruleset.</param>
        public RunWithSchedule(RuleScheduler scheduler, Ruleset ruleset)
        {
            Scheduler = scheduler;
            Ruleset = ruleset;
        }

        /// <summary>
        /// Gets the scheduler.
        /// </summary>
        public RuleScheduler Scheduler { get; }

        /// <summary>
        /// Gets the ruleset.
        /// </summary>
        public Ruleset Ruleset { get; }

        /// <inheritdoc />
        internal override bool Execute(ScheduleExecutor executor, Scope scope, int depth, bool inCase, bool splitsEnabled)
        {
            if ((inCase || scope.IsCase || executor.Options.StopOnContradiction) && RunSchedule.IsContradictory(scope.Engine))
            {
                if (ReferenceEquals(scope, executor.Top))
                    executor.Statistics.StopReason = RunStopReason.Contradiction;

                return false;
            }

            return executor.RunSchedulerStep(scope, Scheduler, Ruleset, inCase);
        }

        /// <inheritdoc />
        internal override void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions, ISet<SplitRule> splits, ISet<Ruleset> inCases,
            bool inCase)
        {
            rulesets.Add(Ruleset);
            if (inCase)
                inCases.Add(Ruleset);
        }

        /// <inheritdoc />
        internal override void CollectSchedulerSteps(ICollection<RunWithSchedule> steps) => steps.Add(this);

        /// <summary>
        /// Checks, before any work, that the step can run in <paramref name="scope"/>: its scheduler is not removed, and
        /// every rule of the ruleset it reaches there can be scheduled, its head using a variable of its body, under a
        /// name no other of them has, since a scheduler tells rules apart by name.
        /// </summary>
        /// <param name="scope">The scope the run starts in.</param>
        /// <exception cref="ArgumentException">The scheduler was removed, a rule cannot be scheduled, or two rules share a
        /// name.</exception>
        internal void Check(Scope scope)
        {
            if (Scheduler.IsRemoved)
                throw new ArgumentException($"Scheduler {Scheduler.Name} was removed.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var compiled in ScheduleExecutor.RulesFor([Ruleset], scope))
            {
                if (ScheduledRule.HasFreeVariables(compiled.Program) == false)
                    throw new ArgumentException(ScheduledRule.NoFreeVariables(compiled.Rule));

                if (names.Add(compiled.Rule.Name) == false)
                    throw new ArgumentException($"Ruleset '{Ruleset.Name}' has two rules named '{compiled.Rule.Name}', which "
                        + $"{Scheduler.Name} cannot tell apart: a scheduler knows rules by name, and egglog at 5e655cae refuses the "
                        + "second.");
            }
        }

    }

}
