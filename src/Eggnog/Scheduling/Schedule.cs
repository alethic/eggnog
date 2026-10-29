using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// egglog's schedules, plus case splits: what <see cref="Scope.Run(Schedule, RunOptions, System.Threading.CancellationToken)"/>
    /// runs. A ruleset converts to <see cref="Run(Ruleset[])"/> of itself. Immutable; renders as an egglog schedule.
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
        public static Schedule Run(params Ruleset[] rulesets) => new RunSchedule(Copy(rulesets, nameof(rulesets)));

        /// <summary>
        /// Up to <paramref name="times"/> passes of <see cref="Sequence"/> of the steps; stops after a pass with no change.
        /// </summary>
        /// <param name="times">The most passes; zero runs nothing.</param>
        /// <param name="steps">The steps of a pass.</param>
        /// <returns>The schedule.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="times"/> is negative.</exception>
        public static Schedule Repeat(int times, params Schedule[] steps)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(times);
            return new RepeatSchedule(times, Copy(steps, nameof(steps)));
        }

        /// <summary>
        /// Passes of <see cref="Sequence"/> of the steps until one changes nothing.
        /// </summary>
        /// <param name="steps">The steps of a pass.</param>
        /// <returns>The schedule.</returns>
        public static Schedule Saturate(params Schedule[] steps) => new SaturateSchedule(Copy(steps, nameof(steps)));

        /// <summary>
        /// The steps in order, once each.
        /// </summary>
        /// <param name="steps">The steps.</param>
        /// <returns>The schedule.</returns>
        public static Schedule Sequence(params Schedule[] steps) => new SequenceSchedule(Copy(steps, nameof(steps)));

        /// <summary>
        /// Runs <paramref name="body"/> unless every fact of <paramref name="condition"/> already holds (egglog's
        /// <c>:until</c>). The condition is a host check (<see cref="Scope.Check(Fact[])"/>) made each time the step
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
        public static Schedule Until(Schedule body, params Fact[] condition)
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
        /// A split step: where the split rule's body matches in the scope, it opens one case per element of the matched set
        /// (each assuming its element), runs <paramref name="inCase"/> in each case, and lifts into the scope what every
        /// live case proves; a case found contradictory has no model and is dropped. The cases live until the top-level run
        /// ends: a later execution of the step skips a key a live split covers in the scope or an ancestor, runs
        /// <paramref name="inCase"/> again in the cases it opened, and lifts what they derived from the scope's new facts.
        /// Nested split steps in <paramref name="inCase"/> split the cases further, after the lift (lift before deepening),
        /// up to <see cref="RunOptions.SplitDepthLimit"/>; a split with more cases than <see cref="RunOptions.CaseLimit"/> is
        /// skipped.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Run it once the facts its rule reads have settled, as in <c>Saturate(Saturate(horn, lattice), Split(rule,
        /// inCase), rewrite)</c>: a split made before its domain narrowed opens cases that go vacuous later, and its record
        /// keeps the key until the run ends, so later steps do not split it again more narrowly.
        /// </para>
        /// <para>
        /// To bound each case, pass <c>Repeat(n, steps)</c> as <paramref name="inCase"/>, with rulesets as the steps; not
        /// <c>Saturate(steps)</c>, nor <c>Repeat(n, Saturate(steps))</c>, each of whose passes is a full saturation.
        /// A case that does not terminate under <c>Saturate</c> keeps the run going, since a run has no iteration limit
        /// unless one is set; <see cref="RunOptions.IterationLimit"/> counts the iterations of every case, so such a case
        /// uses up a limit that is set, and when that happens before the step lifts, the scope gets nothing from any of
        /// the step's cases, those that saturated included. The bound applies to each execution of
        /// <paramref name="inCase"/> in a case: once before the step lifts, once more after it when the step deepens
        /// (below <see cref="RunOptions.SplitDepthLimit"/>), and again at each later execution of the step, so a case can
        /// run <c>n</c> passes several times in one run. A case cut off this way stays live: what it derived holds under
        /// its assumption, the lift keeps what it and its siblings prove, and <see cref="RunReport.StopReason"/> does not
        /// show the cut: a run that otherwise ends normally reports <see cref="RunStopReason.Completed"/>. A host can tell
        /// from <see cref="RunReport.Steps"/>, which stays empty unless <see cref="RunOptions.RecordSteps"/> is set, or
        /// through <see cref="RunOptions.Observer"/>, that a case was cut off: an execution of <paramref name="inCase"/>
        /// in the case ran all <c>n</c> passes, and its last pass still changed the case. Size <c>n</c> from the passes
        /// the cases take when they do terminate, read from the same steps.
        /// </para>
        /// </remarks>
        /// <param name="rule">The split rule.</param>
        /// <param name="inCase">The schedule run in each case; a case always stops on contradiction. <c>Repeat(n,
        /// steps)</c> bounds each case.</param>
        /// <returns>The schedule.</returns>
        public static Schedule Split(SplitRule rule, Schedule inCase)
        {
            ArgumentNullException.ThrowIfNull(rule);
            return Split([rule], inCase);
        }

        /// <summary>
        /// A split step over several split rules: <see cref="Split(SplitRule, Schedule)"/>, with the requests of every rule,
        /// in order. A live split covers a later request for the same key only when both come from the same rule at the same
        /// position of a split step's rules.
        /// </summary>
        /// <param name="rules">The split rules: at least one, each once.</param>
        /// <param name="inCase">The schedule run in each case; a case always stops on contradiction.</param>
        /// <returns>The schedule.</returns>
        /// <exception cref="ArgumentException">There is no rule, or a rule is listed twice.</exception>
        public static Schedule Split(IReadOnlyList<SplitRule> rules, Schedule inCase)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(inCase);
            if (rules.Count == 0)
                throw new ArgumentException("A split step needs at least one split rule.", nameof(rules));

            var copy = new SplitRule[rules.Count];
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = rules[i] ?? throw new ArgumentNullException(nameof(rules), $"Item {i} is null.");
                for (var j = 0; j < i; j++)
                    if (ReferenceEquals(copy[j], copy[i]))
                        throw new ArgumentException($"Split rule '{copy[i].Name}' is listed twice.", nameof(rules));
            }

            return new SplitSchedule(copy, inCase);
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
        /// <param name="depth">The split depth; 0 at the top level.</param>
        /// <param name="inCase">Whether the scope is a case of a split.</param>
        /// <param name="splitsEnabled">Whether split steps run.</param>
        /// <returns>Whether the schedule changed the scope.</returns>
        internal abstract bool Execute(ScheduleExecutor executor, Scope scope, int depth, bool inCase, bool splitsEnabled);

        /// <summary>
        /// Collects the rulesets the schedule reaches, the facts of its <c>Until</c> conditions, and its split rules, which
        /// <c>Scope.Run</c> checks before any work; and the rulesets it runs in the cases of a split.
        /// </summary>
        /// <param name="rulesets">The set to add to.</param>
        /// <param name="conditions">The list to add the condition facts to.</param>
        /// <param name="splits">The set to add the split rules to.</param>
        /// <param name="inCases">The set to add the rulesets that run in the cases of a split to.</param>
        /// <param name="inCase">Whether this schedule runs in the cases of a split.</param>
        internal abstract void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions, ISet<SplitRule> splits, ISet<Ruleset> inCases,
            bool inCase);

        /// <summary>
        /// Collects the scheduler steps the schedule reaches (<see cref="RunWith"/>), whose rules <c>Scope.Run</c> checks
        /// before any work, besides what <see cref="Collect"/> checks.
        /// </summary>
        /// <param name="steps">The list to add the steps to.</param>
        internal virtual void CollectSchedulerSteps(ICollection<RunWithSchedule> steps)
        {

        }

        /// <summary>
        /// Copies an array of non-null items.
        /// </summary>
        static T[] Copy<T>(T[] items, string parameter) where T : class
        {
            ArgumentNullException.ThrowIfNull(items, parameter);
            var copy = new T[items.Length];
            for (var i = 0; i < items.Length; i++)
                copy[i] = items[i] ?? throw new ArgumentNullException(parameter, $"Item {i} is null.");

            return copy;
        }

    }

}
