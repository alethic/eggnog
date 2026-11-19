using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// egglog's schedules: what <see cref="Scope.Run(Schedule, RunOptions, System.Threading.CancellationToken)"/> runs.
    /// A ruleset converts to <see cref="Run(ReadOnlySpan{Ruleset})"/> of itself. Immutable; renders as an egglog
    /// schedule.
    /// </summary>
    public abstract class Schedule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        private protected Schedule()
        {

        }

        /// <summary>
        /// One iteration over the union of the rulesets' rules active in the scope.
        /// </summary>
        /// <param name="rulesets">The rulesets.</param>
        /// <returns>The schedule.</returns>
        public static Schedule Run(params ReadOnlySpan<Ruleset> rulesets) => new RunSchedule(Copy(rulesets, nameof(rulesets)));

        /// <summary>
        /// Up to <paramref name="times"/> passes of <see cref="Sequence"/> of the steps; stops after a pass with no change.
        /// </summary>
        /// <param name="times">The most passes; zero runs nothing.</param>
        /// <param name="steps">The steps of a pass.</param>
        /// <returns>The schedule.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="times"/> is negative.</exception>
        public static Schedule Repeat(int times, params ReadOnlySpan<Schedule> steps)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(times);
            return new RepeatSchedule(times, Copy(steps, nameof(steps)));
        }

        /// <summary>
        /// Passes of <see cref="Sequence"/> of the steps until one changes nothing.
        /// </summary>
        /// <param name="steps">The steps of a pass.</param>
        /// <returns>The schedule.</returns>
        public static Schedule Saturate(params ReadOnlySpan<Schedule> steps) => new SaturateSchedule(Copy(steps, nameof(steps)));

        /// <summary>
        /// The steps in order, once each.
        /// </summary>
        /// <param name="steps">The steps.</param>
        /// <returns>The schedule.</returns>
        public static Schedule Sequence(params ReadOnlySpan<Schedule> steps) => new SequenceSchedule(Copy(steps, nameof(steps)));

        /// <summary>
        /// Runs <paramref name="body"/> unless every fact of <paramref name="condition"/> already holds (egglog's
        /// <c>:until</c>). The condition is a host check (<see cref="Scope.Check(ReadOnlySpan{Fact})"/>) made each time the step
        /// runs, so <c>Saturate(Until(body, facts))</c> runs the body until the facts hold or it changes nothing.
        /// <see cref="Scope.Run(Schedule, RunOptions, System.Threading.CancellationToken)"/> checks, before any work, that
        /// every fact can be read in its scope (no table, column type or class of another graph, no class it cannot use).
        /// </summary>
        /// <param name="body">The schedule.</param>
        /// <param name="condition">Ground facts, at least one: relation rows, equalities, existences, over constants and
        /// table applications.</param>
        /// <returns>The schedule.</returns>
        /// <exception cref="ArgumentException">There is no fact (a forgotten condition would make the step a silent no-op),
        /// or a fact is not a ground host check: a filter (<c>IsNot</c>, <c>Fact.True</c>), or a term with a variable or a
        /// computation.</exception>
        public static Schedule Until(Schedule body, params ReadOnlySpan<Fact> condition)
        {
            ArgumentNullException.ThrowIfNull(body);
            var facts = Copy(condition, nameof(condition));
            if (facts.Length == 0)
                throw new ArgumentException("An until condition needs at least one fact.", nameof(condition));

            foreach (var fact in facts)
                if (TermReader.Validate(null, fact) is { } error)
                    throw new ArgumentException($"An until condition is a host check of ground facts; {error}: {fact}.", nameof(condition));

            return new UntilSchedule(body, facts);
        }

        /// <summary>
        /// One step of <paramref name="ruleset"/> under <paramref name="scheduler"/>: egglog's
        /// <c>step_rules_with_scheduler</c>, and egglog-experimental's <c>run-with</c>. Each rule's query finds its new
        /// matches (semi-naive), the scheduler chooses which of them and of those it left before fire
        /// (<see cref="IRuleScheduler.FilterMatches"/>), and the chosen ones fire; the rest wait for the next step. The
        /// step changes the scope when a head does; one that changes nothing ends a <see cref="Repeat"/> or a
        /// <see cref="Saturate"/> only when the scheduler says it can stop (<see cref="IRuleScheduler.CanStop"/>,
        /// <see cref="RunReport.CanStop"/>): egglog's <c>can_stop</c>, on which egglog-experimental's <c>saturate</c> stops.
        /// <see cref="BackOffScheduler"/> is egglog-experimental's back-off scheduler.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each scope that steps the scheduler holds its own clone of it and its own matches left. A child starts from
        /// what its parent held when the child was made, as egglog's <c>push</c> clones the e-graph: the matches its parent
        /// left wait in the child too, and the child's queries continue from its parent's. A child made before its parent
        /// stepped starts afresh.
        /// </para>
        /// <para>
        /// As egglog at 5e655cae, a rule's query matches subsumed rows too, and a rule whose head uses no variable of its
        /// body cannot be scheduled. The matches offered hold classes as their canonical members in the scope, where
        /// egglog's left matches keep the ids they were found with.
        /// </para>
        /// </remarks>
        /// <param name="scheduler">The scheduler, of the ruleset's graph.</param>
        /// <param name="ruleset">The ruleset.</param>
        /// <returns>The schedule.</returns>
        /// <exception cref="ArgumentException">The scheduler and the ruleset belong to different graphs.</exception>
        public static Schedule RunWith(RuleScheduler scheduler, Ruleset ruleset)
        {
            ArgumentNullException.ThrowIfNull(scheduler);
            ArgumentNullException.ThrowIfNull(ruleset);
            if (ReferenceEquals(scheduler.Graph, ruleset.Graph) == false)
                throw new ArgumentException(
                    $"Scheduler {scheduler.Name} and ruleset '{ruleset.Name}' belong to different graphs.", nameof(ruleset));

            return new RunWithSchedule(scheduler, ruleset);
        }

        /// <summary>
        /// Renders the schedule as an egglog schedule: <c>(saturate (run horn) (run rewrite))</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => SExpression.Render(this);

        /// <summary>
        /// Executes the schedule in <paramref name="scope"/>.
        /// </summary>
        /// <param name="executor">The run's executor.</param>
        /// <param name="scope">The scope to run in.</param>
        /// <returns>Whether the schedule changed the scope.</returns>
        internal abstract bool Execute(ScheduleExecutor executor, Scope scope);

        /// <summary>
        /// Collects the rulesets the schedule reaches and the facts of its <c>Until</c> conditions, which
        /// <c>Scope.Run</c> checks before any work.
        /// </summary>
        /// <param name="rulesets">The set to add to.</param>
        /// <param name="conditions">The list to add the condition facts to.</param>
        internal abstract void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions);

        /// <summary>
        /// Collects the scheduler steps the schedule reaches (<see cref="RunWith"/>), whose rules <c>Scope.Run</c> checks
        /// before any work, besides what <see cref="Collect"/> checks.
        /// </summary>
        /// <param name="steps">The list to add the steps to.</param>
        internal virtual void CollectSchedulerSteps(ICollection<RunWithSchedule> steps)
        {

        }

        /// <summary>
        /// Copies a span of non-null items.
        /// </summary>
        static T[] Copy<T>(ReadOnlySpan<T> items, string parameter) where T : class
        {
            var copy = new T[items.Length];
            for (var i = 0; i < items.Length; i++)
                copy[i] = items[i] ?? throw new ArgumentNullException(parameter, $"Item {i} is null.");

            return copy;
        }

    }

}
