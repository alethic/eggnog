using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    public sealed partial class Scope
    {

        /// <summary>
        /// Runs a schedule in this scope (§6.5).
        /// </summary>
        /// <param name="schedule">The schedule.</param>
        /// <param name="options">The run's options; <c>null</c> for <see cref="RunOptions.Default"/>.</param>
        /// <param name="cancellationToken">Cancels the run between iterations.</param>
        /// <returns>What the run did.</returns>
        /// <exception cref="ArgumentException">A ruleset of the schedule belongs to another graph, or an <c>Until</c>
        /// condition cannot be checked here: it names a table or column type of another graph, or a class this scope cannot
        /// use (D39). Both are refused before any work, so nothing is written.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The iteration, time or row limit is negative.</exception>
        /// <exception cref="InvalidOperationException">The scope is frozen or faulted; a rule the schedule reaches reads a
        /// tag no ancestor-or-self of the scope carries (before any work); or the call comes from another thread while the
        /// scope is in use, from a primitive, lattice, filter or cost function, or from a run observer.</exception>
        /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
        /// <exception cref="RuleEvaluationException">An evaluation failure; the scope is then faulted.</exception>
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
                // Everything the schedule reaches is checked before any work (§6.5.3): a condition that fails only when its
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

                // Every tag a reachable rule reads must resolve from here (§6.5.3, §6.7).
                foreach (var rule in ScheduleExecutor.RulesFor(new List<Ruleset>(rulesets), this))
                    foreach (var tag in rule.Program.ReadScopes)
                        ScopeResolver.Resolve(this, tag, $"Rule '{rule.Rule.Name}'");

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

                return executor.Complete(reason, executor.Clock);
            }
            finally
            {
                Exit(true);
            }
        }

        /// <summary>
        /// Creates an extractor over this scope as it is now (§6.10). Reads, queries and writes that change nothing never
        /// invalidate it; a write that changes the scope, or a run, may.
        /// </summary>
        /// <param name="costs">The cost model; <c>null</c> for <see cref="CostModel.Default"/>.</param>
        /// <returns>The extractor.</returns>
        /// <exception cref="ArgumentException">The cost model names a constructor or column type of another graph.</exception>
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
        /// <exception cref="InvalidOperationException">No finite term exists.</exception>
        public ExtractedTerm Extract<TSort>(TSort root, CostModel? costs = null) where TSort : struct, ISort<TSort> =>
            CreateExtractor(costs).Extract(root);

    }

}
