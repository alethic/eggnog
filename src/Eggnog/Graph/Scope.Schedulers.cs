using System.Collections.Generic;

namespace Eggnog
{

    public sealed partial class Scope
    {

        Dictionary<RuleScheduler, SchedulerState>? schedulerStates;
        Dictionary<RuleScheduler, SchedulerState>? schedulerSnapshot;

        /// <summary>
        /// Gets the state of <paramref name="scheduler"/> this scope steps, making it its own: its own state, copied first
        /// when a child took it; else a copy of the state its parent had when the scope was made, whose queries' progress
        /// the step then sets in this scope's context; else a fresh clone of the scheduler. Called under the scope's
        /// guard.
        /// </summary>
        /// <param name="scheduler">The scheduler.</param>
        /// <returns>The state, which this scope alone may change.</returns>
        /// <exception cref="System.InvalidOperationException">The scheduler's clone is <c>null</c>.</exception>
        internal SchedulerState TakeSchedulerState(RuleScheduler scheduler)
        {
            if (schedulerStates is not null && schedulerStates.TryGetValue(scheduler, out var own))
            {
                if (own.Captured)
                {
                    own = own.Copy(seeded: own.Seeded);
                    schedulerStates[scheduler] = own;
                }

                return own;
            }

            var state = schedulerSnapshot is not null && schedulerSnapshot.TryGetValue(scheduler, out var inherited)
                ? inherited.Copy(seeded: false)
                : new SchedulerState(SchedulerState.Clone(scheduler.Prototype), new Dictionary<ScheduledRule, ScheduledRuleState>(),
                    seeded: true);
            (schedulerStates ??= new Dictionary<RuleScheduler, SchedulerState>(ReferenceEqualityComparer.Instance))[scheduler] = state;
            return state;
        }

        /// <summary>
        /// Takes the states of this scope's schedulers for a child made now: its own, and for a scheduler it never
        /// stepped, the one it took from its parent; <c>null</c> when there is none. Each own state is marked taken, so
        /// that neither scope changes it again.
        /// </summary>
        /// <returns>The states, by scheduler.</returns>
        Dictionary<RuleScheduler, SchedulerState>? CaptureSchedulerStates()
        {
            if (schedulerStates is null)
                return schedulerSnapshot;

            var snapshot = schedulerSnapshot is null
                ? new Dictionary<RuleScheduler, SchedulerState>(ReferenceEqualityComparer.Instance)
                : new Dictionary<RuleScheduler, SchedulerState>(schedulerSnapshot, ReferenceEqualityComparer.Instance);
            foreach (var (scheduler, state) in schedulerStates)
            {
                state.Captured = true;
                snapshot[scheduler] = state;
            }

            return snapshot;
        }

    }

}
