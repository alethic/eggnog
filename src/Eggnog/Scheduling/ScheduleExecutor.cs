using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// Runs one top-level <see cref="Scope.Run(Schedule, RunOptions, CancellationToken)"/>: chooses the rules of each
    /// iteration, checks cancellation and limits before each, runs it, and accumulates the report (§6.5).
    /// </summary>
    internal sealed class ScheduleExecutor
    {

        readonly CancellationToken cancellationToken;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="top">The scope the run was called on.</param>
        /// <param name="options">The run's options.</param>
        /// <param name="cancellationToken">The run's cancellation token.</param>
        public ScheduleExecutor(Scope top, RunOptions options, CancellationToken cancellationToken)
        {
            Top = top;
            Options = options;
            this.cancellationToken = cancellationToken;
        }

        /// <summary>
        /// Gets the scope the run was called on.
        /// </summary>
        public Scope Top { get; }

        /// <summary>
        /// Gets the run's options.
        /// </summary>
        public RunOptions Options { get; }

        /// <summary>
        /// Gets what the run has done so far.
        /// </summary>
        public RunStatistics Statistics { get; } = new();

        /// <summary>
        /// Gets the rules of an iteration: the rulesets' global rules (snapshots, in definition order), deduplicated by
        /// reference. Recomputed for every iteration, so rules defined meanwhile join the next one (D6). R2 appends the
        /// local rules of the scope and its ancestors.
        /// </summary>
        /// <param name="rulesets">The rulesets.</param>
        /// <param name="scope">The scope the iteration runs in.</param>
        /// <returns>The rules.</returns>
        public IReadOnlyList<CompiledRule> RulesFor(IReadOnlyList<Ruleset> rulesets, Scope scope)
        {
            if (rulesets.Count == 1)
                return rulesets[0].Compiled;

            var seen = new HashSet<CompiledRule>(ReferenceEqualityComparer.Instance);
            var rules = new List<CompiledRule>();
            foreach (var ruleset in rulesets)
                foreach (var rule in ruleset.Compiled)
                    if (seen.Add(rule))
                        rules.Add(rule);

            return rules;
        }

        /// <summary>
        /// Checks, before an iteration, cancellation and then the iteration limit (D42).
        /// </summary>
        /// <param name="scope">The scope about to iterate.</param>
        /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
        /// <exception cref="ScheduleStopException">A limit was reached.</exception>
        public void BeforeStep(Scope scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Statistics.Iterations >= Options.IterationLimit)
                throw new ScheduleStopException(RunStopReason.IterationLimit);
        }

        /// <summary>
        /// Runs one engine iteration of <paramref name="rules"/> in <paramref name="scope"/> (§6.5.1).
        /// </summary>
        /// <param name="scope">The scope.</param>
        /// <param name="rules">The rules.</param>
        /// <param name="inCase">Whether the scope is a case (R3).</param>
        /// <returns>Whether the iteration changed the scope (its rules' writes or the rebuild).</returns>
        /// <exception cref="RuleEvaluationException">An evaluation failure; the scope is faulted.</exception>
        public bool RunIteration(Scope scope, IReadOnlyList<CompiledRule> rules, bool inCase)
        {
            BeforeStep(scope);
            if (rules.Count == 0)
                return false;

            var engine = scope.Engine;
            engine.EnsureClean();
            var lowered = engine.PrepareRules(rules, Options.SemiNaive);
            var ids = new Relations.EGraphRuleId[lowered.Length];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = lowered[i].Id;

            scope.Runtime.TakeFailure();
            EngineIteration iteration;
            try
            {
                iteration = engine.RunRules(ids);
            }
            catch (Relations.PanicException panic)
            {
                var failure = scope.Runtime.TakeFailure();
                var exception = new RuleEvaluationException(failure?.RuleName, failure?.Bindings, failure?.What ?? panic.PanicMessage,
                    failure?.Exception ?? panic);
                scope.Fault(exception);
                throw exception;
            }

            Statistics.Iterations++;
            var report = iteration.Report;
            foreach (var variant in lowered)
            {
                if (variant.Rule is not { } rule)
                    continue;

                if (Statistics.Rules.TryGetValue(rule, out var builder) == false)
                    Statistics.Rules.Add(rule, builder = new RuleStatisticsBuilder());

                builder.Add(report.NumMatches(variant.Description), report.RuleSearchAndApplyTime(variant.Description), null);
            }

            Statistics.SearchTime += report.SearchAndApplyTime;
            Statistics.MergeTime += report.MergeTime;
            Statistics.RebuildTime += iteration.RebuildTime;
            var changed = iteration.Changed || iteration.RebuildChanged;
            if (ReferenceEquals(scope, Top))
            {
                Statistics.LastTopStepChanged = changed;
                Statistics.TopChanged |= changed;
            }

            return changed;
        }

        /// <summary>
        /// Builds the run's report.
        /// </summary>
        /// <param name="reason">Why the run ended.</param>
        /// <param name="clock">The run's stopwatch.</param>
        /// <returns>The report.</returns>
        public RunReport Complete(RunStopReason reason, Stopwatch clock)
        {
            var rules = new Dictionary<Rule, RuleStatistics>(ReferenceEqualityComparer.Instance);
            foreach (var (rule, builder) in Statistics.Rules)
                rules.Add(rule, builder.Build(rule));

            var saturated = reason == RunStopReason.Completed && Statistics.LastTopStepChanged == false;
            return new RunReport(Statistics.Iterations, Statistics.TopChanged, saturated, reason, clock.Elapsed, new ReadOnlyDictionary<Rule, RuleStatistics>(rules),
                Statistics.SearchTime, Statistics.MergeTime, Statistics.RebuildTime);
        }

    }

}
