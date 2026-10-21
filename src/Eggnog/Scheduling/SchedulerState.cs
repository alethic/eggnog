using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// One scope's state of one scheduler: its own clone of the scheduler and what it holds for each rule
    /// (<see cref="ScheduledRuleState"/>). A child starts from its parent's as it was when the child was made, as
    /// egglog's <c>push</c> clones the e-graph with its schedulers: once a child has taken a state
    /// (<see cref="Captured"/>), neither side changes it, and the side that steps next copies it first.
    /// </summary>
    internal sealed class SchedulerState
    {

        volatile bool captured;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="instance">The scheduler, this state's own.</param>
        /// <param name="rules">What it holds for each rule.</param>
        /// <param name="seeded">Whether the queries' progress the rules' states carry is the owning scope's own; a
        /// state copied from an ancestor's sets it in the child's context at the first step.</param>
        public SchedulerState(IRuleScheduler instance, Dictionary<ScheduledRule, ScheduledRuleState> rules, bool seeded)
        {
            Instance = instance;
            Rules = rules;
            Seeded = seeded;
        }

        /// <summary>
        /// Gets the scheduler.
        /// </summary>
        public IRuleScheduler Instance { get; }

        /// <summary>
        /// Gets what the scheduler holds for each rule it has stepped; changed only by the owning scope's steps, and
        /// only while the state is not <see cref="Captured"/>.
        /// </summary>
        public Dictionary<ScheduledRule, ScheduledRuleState> Rules { get; }

        /// <summary>
        /// Gets or sets whether the queries' progress in <see cref="Rules"/> is in the owning scope's context.
        /// </summary>
        public bool Seeded { get; set; }

        /// <summary>
        /// Gets or sets whether a child has taken the state, which then never changes. Children of a frozen scope are made
        /// on any thread; each sets the flag, and the frozen scope never steps.
        /// </summary>
        public bool Captured
        {
            get => captured;
            set => captured = value;
        }

        /// <summary>
        /// Copies the state for a scope to step: a clone of the scheduler and a copy of what it holds.
        /// </summary>
        /// <param name="seeded">Whether the progress is the copying scope's own: the owner's when it copies a state a
        /// child took, not a child's copying its parent's.</param>
        /// <returns>The copy.</returns>
        /// <exception cref="System.InvalidOperationException">The scheduler's clone is <c>null</c>.</exception>
        public SchedulerState Copy(bool seeded) =>
            new(Clone(Instance), new Dictionary<ScheduledRule, ScheduledRuleState>(Rules), seeded);

        /// <summary>
        /// Clones a scheduler, refusing a <c>null</c> clone.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">The clone is <c>null</c>.</exception>
        public static IRuleScheduler Clone(IRuleScheduler scheduler) =>
            scheduler.Clone() ?? throw new System.InvalidOperationException($"{scheduler.GetType().Name}.Clone returned null.");

    }

}
