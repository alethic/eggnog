using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    public sealed partial class Scope
    {

        /// <summary>
        /// The refusal of a rule that writes a function whose merge is not a lattice where it would run below the root.
        /// </summary>
        static InvalidOperationException RootOnly(CompiledRule rule, FunctionHandle function, string why) =>
            new($"Rule '{rule.Rule.Name}' writes '{function.Name}', whose merge '{function.Merge!.Name}' is not a lattice, so only the root may run it, and {why}.");

        /// <summary>
        /// Runs a schedule in this scope.
        /// </summary>
        /// <param name="schedule">The schedule.</param>
        /// <param name="options">The run's options; <c>null</c> for <see cref="RunOptions.Default"/>.</param>
        /// <param name="cancellationToken">Cancels the run between iterations; an iteration already started runs to its
        /// end.</param>
        /// <returns>What the run did.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentException">A ruleset of the schedule belongs to another graph, or an <c>Until</c>
        /// condition cannot be checked here: it names a table or column type of another graph, or a class this scope
        /// cannot use. All are refused before any work, so nothing is written.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The iteration, time or row limit is negative.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen or faulted; a rule the schedule reaches
        /// reads a tag no ancestor-or-self of the scope carries, or a rule that would run below the root writes a
        /// function whose merge is not a lattice, or a rule deletes and the scope may not delete (it has live
        /// descendants or a writable parent) (all before any work); or the call comes from another thread while the
        /// scope is in use, from a primitive, lattice, filter or cost function, or from a run observer.</exception>
        /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
        /// <exception cref="RuleEvaluationException">An evaluation failure in an iteration of this scope, a primitive's,
        /// a lambda's, a callback's or a merge's: as egglog's failed run, the failure stops the iteration, whose matches
        /// applied before it stopped keep what they wrote before a call of theirs failed, merged and rebuilt; the iteration
        /// counts as run, so a later run does not apply the matches of a semi-naive rule it did not reach, as egglog's; and
        /// the scope is not faulted. Or a scheduler threw: the scope is then faulted. Or a copy rule failed in the sync of
        /// a mirror that a rule of the run reads: the scope is faulted, and so is the tagged scope the sync ran in. Or a
        /// lattice merge threw in the catch-up the call makes first, here or in a writable ancestor: the scope is
        /// faulted, and so is the ancestor.</exception>
        /// <exception cref="OverflowException">The graph has run out of ids or epochs (<see cref="EGraph.Usage"/>): the
        /// step that ran out changes nothing, what the run's earlier iterations wrote stays, and the scope is not
        /// faulted, but every later call that needs an id or an epoch throws the same way. A rule that makes a term
        /// needs an id, and every iteration and rebuild pass needs epochs.</exception>
        public RunReport Run(Schedule schedule, RunOptions? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            options ??= RunOptions.Default;
            if (options.IterationLimit < 0)
                throw new ArgumentOutOfRangeException(nameof(options), options.IterationLimit, "The iteration limit must not be negative.");

            if (options.TimeLimit < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), options.TimeLimit, "The time limit must not be negative.");

            if (options.RowLimit < 0)
                throw new ArgumentOutOfRangeException(nameof(options), options.RowLimit, "The row limit must not be negative.");

            EnterWrite();
            try
            {
                // Everything the schedule reaches is checked before any work: a condition that fails only when its
                // Until node executes would leave the earlier steps' writes behind and lose the run's report.
                var rulesets = new HashSet<Ruleset>(ReferenceEqualityComparer.Instance);
                var conditions = new List<Fact>();
                schedule.Collect(rulesets, conditions);
                foreach (var ruleset in rulesets)
                    if (ReferenceEquals(ruleset.Graph, Graph) == false)
                        throw new ArgumentException($"Ruleset '{ruleset.Name}' belongs to another graph.", nameof(schedule));

                foreach (var fact in conditions)
                    if (TermReader.Validate(this, fact) is { } error)
                        throw new ArgumentException($"An until condition cannot be checked in scope '{Path}': {error}: {fact}.", nameof(schedule));

                // One pass over the reachable rules, listed once: a query runs here too, so these checks stay cheap per
                // rule. Every tag a reachable rule reads must resolve from here. Only the root writes a function whose
                // merge is not a lattice, so no rule that writes one runs in this scope when it is a child. And egglog's
                // delete: the engine's retraction guard lets a scope delete only with no live descendant and no writable
                // parent.
                var reachable = ScheduleExecutor.RulesFor(new List<Ruleset>(rulesets), this);
                for (var r = 0; r < reachable.Count; r++)
                {
                    var rule = reachable[r];
                    foreach (var tag in rule.Program.ReadScopes)
                        ScopeResolver.Resolve(this, tag, $"Rule '{rule.Rule.Name}'");

                    if (IsRoot == false && rule.Program.RootOnlyWrite is { } function)
                        throw RootOnly(rule, function, $"'{Path}' is not the root");

                    if (rule.Program.Deletes is [var deleted, ..] && Engine.RetractionRefusal is { } why)
                        throw new InvalidOperationException($"Rule '{rule.Rule.Name}' deletes rows of '{deleted.Name}', and '{Path}' may not delete: {why}.");
                }

                var steps = new List<RunWithSchedule>();
                schedule.CollectSchedulerSteps(steps);
                foreach (var step in steps)
                    step.Check(this);

                var executor = new ScheduleExecutor(this, options, cancellationToken);
                RunStopReason reason;
                try
                {
                    schedule.Execute(executor, this);
                    reason = RunStopReason.Completed;
                }
                catch (ScheduleStopException stop)
                {
                    reason = stop.Reason;
                }
                catch (RuleEvaluationException failure) when (executor.ObserverFailed == false && executor.RunFailed == false)
                {
                    // A failure with no egglog counterpart, such as a scheduler's or a mirror's, fails the run: the
                    // scope is faulted too, with the same cause. One of this scope's own iterations fails the run as
                    // egglog's does, and leaves the scope usable.
                    Fault(failure);
                    throw;
                }

                return executor.Complete(reason, executor.Clock);
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Creates an extractor over this scope as it is now. Reads, queries and writes that change nothing never
        /// invalidate it; a write that changes the scope, or a run, may.
        /// </summary>
        /// <param name="costs">The cost model; <c>null</c> for <see cref="CostModel.Default"/>.</param>
        /// <returns>The extractor.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">The scope is faulted; or it is writable and in use on another
        /// thread, or has a <c>Write</c> batch open; or the call comes from a primitive, lattice, filter or cost
        /// function.</exception>
        /// <exception cref="ArgumentException">The cost model names a constructor or column type of another graph.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        public Extractor CreateExtractor(CostModel? costs = null)
        {
            costs ??= CostModel.Default;
            var held = EnterRead();
            try
            {
                var schema = Graph.Extraction.SchemaFor(costs);
                var engineCosts = Graph.Extraction.EngineCosts(costs);
                return new Extractor(this, costs, Engine.CreateExtractor(schema, engineCosts));
            }
            finally
            {
                Exit(held);
            }
        }

        /// <summary>
        /// Extracts the cheapest term of a class here: <c>CreateExtractor(costs).Extract(root)</c>.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="root">The class.</param>
        /// <param name="costs">The cost model; <c>null</c> for <see cref="CostModel.Default"/>.</param>
        /// <returns>The term.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="InvalidOperationException">No finite term exists; or the scope is faulted; or it is
        /// writable and in use on another thread, or has a <c>Write</c> batch open; or the call comes from a primitive,
        /// lattice, filter or cost function.</exception>
        /// <exception cref="ArgumentException">The cost model names a constructor or column type of another graph, or
        /// the class value is not usable in this scope.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        public ExtractedTerm Extract<TSort>(TSort root, CostModel? costs = null) where TSort : struct, ISort<TSort> =>
            CreateExtractor(costs).Extract(root);

        /// <summary>
        /// Extracts up to <paramref name="count"/> variants of a class here, cheapest first:
        /// <c>CreateExtractor(costs).ExtractVariants(root, count)</c>, egglog's <c>(extract x n)</c>.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="root">The class.</param>
        /// <param name="count">The number of variants wanted; at least 0.</param>
        /// <param name="costs">The cost model; <c>null</c> for <see cref="CostModel.Default"/>.</param>
        /// <returns>The variants, cheapest first: <paramref name="count"/> of them, or all the class has if fewer, and
        /// none when the class has no finite term or <paramref name="count"/> is 0.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">The scope is faulted; or it is writable and in use on another
        /// thread, or has a <c>Write</c> batch open; or the call comes from a primitive, lattice, filter or cost
        /// function.</exception>
        /// <exception cref="ArgumentException">The cost model names a constructor or column type of another graph, or
        /// the class value is not usable in this scope.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        public IReadOnlyList<ExtractedTerm> ExtractVariants<TSort>(TSort root, int count, CostModel? costs = null) where TSort : struct, ISort<TSort>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            return CreateExtractor(costs).ExtractVariants(root, count);
        }

    }

}
