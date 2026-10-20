using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// <remarks>
        /// A split step (<see cref="Schedule.Split(SplitRule, Schedule)"/>) opens cases under this scope, runs its
        /// in-case schedule in them and lifts what every live case proves; the cases live until the run ends, when they
        /// are disposed (their <see cref="Scope"/>s report <see cref="IsDisposed"/> and leave <see cref="Children"/>),
        /// whether the run completes, stops at a limit, or throws. On a case whose contradiction stopped it, or a scope
        /// under one, the run returns at once, with no iteration and <see cref="RunStopReason.Contradiction"/>.
        /// </remarks>
        /// <param name="schedule">The schedule.</param>
        /// <param name="options">The run's options; <c>null</c> for <see cref="RunOptions.Default"/>.</param>
        /// <param name="cancellationToken">Cancels the run between iterations and split steps; an iteration or a lift already
        /// started runs to its end.</param>
        /// <returns>What the run did.</returns>
        /// <exception cref="ObjectDisposedException">The scope is disposed; or it is disposed during the run, and a later
        /// split step opens cases directly under it, which the step refuses, after earlier steps may have written. A split
        /// step that opens no case is not refused.</exception>
        /// <exception cref="ArgumentException">A ruleset or split rule of the schedule belongs to another graph, or an
        /// <c>Until</c> condition cannot be checked here: it names a table or column type of another graph, or a class this
        /// scope cannot use. All are refused before any work, so nothing is written.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The iteration, time or row limit is negative, or the case or split
        /// depth limit is less than 1.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen or faulted; a rule or split rule the schedule
        /// reaches reads a tag no ancestor-or-self of the scope carries, or a rule that would run below the root, here or
        /// in a split's cases, writes a function whose merge is not a lattice, or a rule deletes and the scope may not
        /// delete (it has live descendants or a writable parent) or the schedule splits (all before any work); or the
        /// call comes from another thread while the scope is in use, from a primitive, lattice, filter or cost function,
        /// or from a run observer.</exception>
        /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
        /// <exception cref="RuleEvaluationException">An evaluation failure, in this scope or in a case of the run (a split's
        /// assumption that gives a function without a lattice a second value included, and a lattice merge that threw
        /// while a split step caught a case up); the scope is then faulted, and so is the case. Or a lattice merge
        /// threw in the catch-up the call makes first, here or in a writable ancestor: the scope is faulted, and so is
        /// the ancestor.</exception>
        /// <exception cref="OverflowException">The graph has run out of ids or epochs (<see cref="EGraph.Usage"/>): the step
        /// that ran out changes nothing, what the run's earlier iterations and lifts wrote stays, and the scope is not
        /// faulted, but every later call that needs an id or an epoch throws the same way. A rule that makes a term
        /// needs an id, and every iteration, rebuild pass and case needs epochs.</exception>
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

            if (options.CaseLimit < 1)
                throw new ArgumentOutOfRangeException(nameof(options), options.CaseLimit, "The case limit must be at least 1.");

            if (options.SplitDepthLimit < 1)
                throw new ArgumentOutOfRangeException(nameof(options), options.SplitDepthLimit, "The split depth limit must be at least 1.");

            EnterWrite();
            try
            {
                // A stopped case, or a scope under one, has no model: nothing runs in it.
                if (IsStopped)
                    return new ScheduleExecutor(this, options, cancellationToken).Complete(RunStopReason.Contradiction, Stopwatch.StartNew());

                // Everything the schedule reaches is checked before any work: a condition that fails only when its
                // Until node executes would leave the earlier steps' writes behind and lose the run's report.
                var rulesets = new HashSet<Ruleset>(ReferenceEqualityComparer.Instance);
                var conditions = new List<Fact>();
                var splits = new HashSet<SplitRule>(ReferenceEqualityComparer.Instance);
                var inCases = new HashSet<Ruleset>(ReferenceEqualityComparer.Instance);
                schedule.Collect(rulesets, conditions, splits, inCases, false);
                foreach (var ruleset in rulesets)
                    if (ReferenceEquals(ruleset.Graph, Graph) == false)
                        throw new ArgumentException($"Ruleset '{ruleset.Name}' belongs to another graph.", nameof(schedule));

                foreach (var split in splits)
                    if (ReferenceEquals(split.Graph, Graph) == false)
                        throw new ArgumentException($"Split rule '{split.Name}' belongs to another graph.", nameof(schedule));

                foreach (var fact in conditions)
                    if (TermReader.Validate(this, fact) is { } error)
                        throw new ArgumentException($"An until condition cannot be checked in scope '{Path}': {error}: {fact}.", nameof(schedule));

                // One pass over the reachable rules, listed once: a query runs here too, so these checks stay cheap per
                // rule. Every tag a reachable rule reads must resolve from here; a case only adds its own tag below this
                // scope, so what resolves here resolves in every case of the run. Only the root writes a function whose
                // merge is not a lattice, so no rule that writes one runs in this scope when it is a child. And egglog's
                // delete: the engine's retraction guard lets a scope delete only with no live descendant and no writable
                // parent, and a split's cases are children of this scope, live until the run ends.
                var reachable = ScheduleExecutor.RulesFor(new List<Ruleset>(rulesets), this);
                for (var r = 0; r < reachable.Count; r++)
                {
                    var rule = reachable[r];
                    foreach (var tag in rule.Program.ReadScopes)
                        ScopeResolver.Resolve(this, tag, $"Rule '{rule.Rule.Name}'");

                    if (IsRoot == false && rule.Program.RootOnlyWrite is { } function)
                        throw RootOnly(rule, function, $"'{Path}' is not the root");

                    if (rule.Program.Deletes is [var deleted, ..])
                    {
                        if (splits.Count > 0)
                            throw new InvalidOperationException(
                                $"Rule '{rule.Rule.Name}' deletes rows of '{deleted.Name}', and the schedule splits: the cases are children of '{Path}', which may not delete while they are live.");

                        if (Engine.RetractionRefusal is { } why)
                            throw new InvalidOperationException($"Rule '{rule.Rule.Name}' deletes rows of '{deleted.Name}', and '{Path}' may not delete: {why}.");
                    }
                }

                foreach (var split in splits)
                    foreach (var tag in split.Program.Matcher.ReadScopes)
                        ScopeResolver.Resolve(this, tag, $"Split rule '{split.Name}'");

                // The cases of a split are children, at the root too.
                if (IsRoot && inCases.Count > 0)
                    foreach (var rule in ScheduleExecutor.RulesFor(new List<Ruleset>(inCases), this))
                        if (rule.Program.RootOnlyWrite is { } function)
                            throw RootOnly(rule, function, "the cases of a split are children");

                var executor = new ScheduleExecutor(this, options, cancellationToken);
                RunStopReason reason;
                try
                {
                    schedule.Execute(executor, this, 0, false, true);
                    reason = executor.Statistics.StopReason;
                }
                catch (ScheduleStopException stop)
                {
                    reason = stop.Reason;
                }
                catch (RuleEvaluationException failure) when (executor.ObserverFailed == false)
                {
                    // A failure in a case of the run, in a split matcher or in a lift's case join fails the run: the
                    // scope it ran in is faulted too, with the same cause. One in this scope faulted it already.
                    Fault(failure);
                    throw;
                }
                finally
                {
                    // The cases a split step made live until the run ends: disposed depth-first, in reverse
                    // creation order, then their scopes released.
                    Relations.SplitDriver.DisposeRecords(Engine.Context);
                    ReleaseDisposedCases();
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
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        public Extractor CreateExtractor(CostModel? costs = null)
        {
            costs ??= CostModel.Default;
            var held = EnterRead();
            try
            {
                ThrowIfStopped();
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
        /// <exception cref="InvalidOperationException">No finite term exists, or a cost function returned a negative
        /// cost; or the scope is faulted; or it is writable and in use on another thread, or has a <c>Write</c> batch
        /// open; or the call comes from a primitive, lattice, filter or cost function.</exception>
        /// <exception cref="ArgumentException">The cost model names a constructor or column type of another graph, or
        /// the class value is not usable in this scope.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
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
        /// <exception cref="InvalidOperationException">A cost function returned a negative cost; or the scope is
        /// faulted; or it is writable and in use on another thread, or has a <c>Write</c> batch open; or the call comes
        /// from a primitive, lattice, filter or cost function.</exception>
        /// <exception cref="ArgumentException">The cost model names a constructor or column type of another graph, or
        /// the class value is not usable in this scope.</exception>
        /// <exception cref="RuleEvaluationException">A lattice merge threw in the catch-up the call makes first, here or in a
        /// writable ancestor; the scope is faulted, and so is the ancestor.</exception>
        /// <exception cref="ScopeContradictedException">The scope is a case whose contradiction stopped it, or lies
        /// under one.</exception>
        public IReadOnlyList<ExtractedTerm> ExtractVariants<TSort>(TSort root, int count, CostModel? costs = null) where TSort : struct, ISort<TSort>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            return CreateExtractor(costs).ExtractVariants(root, count);
        }

    }

}
