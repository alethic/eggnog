using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Eggnog
{

    public sealed partial class Scope
    {

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
        /// <exception cref="ObjectDisposedException">The scope is disposed.</exception>
        /// <exception cref="ArgumentException">A ruleset or split rule of the schedule belongs to another graph, or an
        /// <c>Until</c> condition cannot be checked here: it names a table or column type of another graph, or a class this
        /// scope cannot use. All are refused before any work, so nothing is written.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The iteration, time or row limit is negative, or the case or split
        /// depth limit is less than 1.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen or faulted; a rule or split rule the schedule
        /// reaches reads a tag no ancestor-or-self of the scope carries (before any work); or the call comes from another
        /// thread while the scope is in use, from a primitive, lattice, filter or cost function, or from a run
        /// observer.</exception>
        /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
        /// <exception cref="RuleEvaluationException">An evaluation failure, in this scope or in a case of the run (a split's
        /// assumption that gives a function without a lattice a second value included, and a lattice merge that threw
        /// while a split step caught a case up); the scope is then faulted, and so is the case. Or a lattice merge
        /// threw in the catch-up the call makes first, here or in a writable ancestor: the scope is faulted, and so is
        /// the ancestor.</exception>
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
                schedule.Collect(rulesets, conditions, splits);
                foreach (var ruleset in rulesets)
                    if (ReferenceEquals(ruleset.Graph, Graph) == false)
                        throw new ArgumentException($"Ruleset '{ruleset.Name}' belongs to another graph.", nameof(schedule));

                foreach (var split in splits)
                    if (ReferenceEquals(split.Graph, Graph) == false)
                        throw new ArgumentException($"Split rule '{split.Name}' belongs to another graph.", nameof(schedule));

                foreach (var fact in conditions)
                    if (TermReader.Validate(this, fact) is { } error)
                        throw new ArgumentException($"An until condition cannot be checked in scope '{Path}': {error}: {fact}.", nameof(schedule));

                // Every tag a reachable rule or split rule reads must resolve from here. A case only adds
                // its own tag below this scope, so what resolves here resolves in every case of the run.
                foreach (var rule in ScheduleExecutor.RulesFor(new List<Ruleset>(rulesets), this))
                    foreach (var tag in rule.Program.ReadScopes)
                        ScopeResolver.Resolve(this, tag, $"Rule '{rule.Rule.Name}'");

                foreach (var split in splits)
                    foreach (var tag in split.Program.Matcher.ReadScopes)
                        ScopeResolver.Resolve(this, tag, $"Split rule '{split.Name}'");

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

    }

}
