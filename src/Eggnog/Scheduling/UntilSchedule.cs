using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The body unless every fact of the condition already holds (egglog's <c>:until</c>, D24, §6.5.2): the condition is
    /// a host check (§6.6.3) in the scope, made each time the node executes, so <c>Saturate(Until(body, facts))</c> runs
    /// the body until the facts hold or nothing changes. <see cref="Schedule.Until"/> checks that the facts are ground
    /// host checks, and <c>Scope.Run</c> that they can be read in its scope, before any work.
    /// </summary>
    internal sealed class UntilSchedule : Schedule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="body">The schedule run while the condition does not hold.</param>
        /// <param name="condition">The ground facts, at least one; the body is skipped when every one holds.</param>
        public UntilSchedule(Schedule body, Fact[] condition)
        {
            Body = body;
            Condition = condition;
        }

        /// <summary>
        /// Gets the schedule run while the condition does not hold.
        /// </summary>
        public Schedule Body { get; }

        /// <summary>
        /// Gets the ground facts of the condition.
        /// </summary>
        public Fact[] Condition { get; }

        /// <inheritdoc />
        internal override bool Execute(ScheduleExecutor executor, Scope scope, int depth, bool inCase, bool splitsEnabled)
        {
            scope.Engine.EnsureClean();
            foreach (var fact in Condition)
                if (TermReader.Check(scope, fact) == false)
                    return Body.Execute(executor, scope, depth, inCase, splitsEnabled);

            return false;
        }

        /// <inheritdoc />
        internal override void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions)
        {
            foreach (var fact in Condition)
                conditions.Add(fact);

            Body.Collect(rulesets, conditions);
        }

    }

}
