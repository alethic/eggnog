using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// Runs one top-level <see cref="Scope.Run(Schedule, RunOptions, CancellationToken)"/>: chooses the rules of each
    /// iteration, checks cancellation and limits before each, runs it, records its step (§6.11), and accumulates the
    /// report (§6.5).
    /// </summary>
    internal sealed class ScheduleExecutor
    {

        readonly CancellationToken cancellationToken;
        readonly bool wantsSteps;

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
            wantsSteps = options.RecordSteps || options.Observer is not null;
            if (options.RecordSteps)
                Statistics.Steps = new List<RunStep>();

            Clock = Stopwatch.StartNew();
        }

        /// <summary>
        /// Gets the run's clock, started with the executor: <see cref="RunOptions.TimeLimit"/> and
        /// <see cref="RunReport.Elapsed"/> read it.
        /// </summary>
        public Stopwatch Clock { get; }

        /// <summary>
        /// Gets the cases of this run that are alive, for <see cref="RunOptions.RowLimit"/>: none before R3.
        /// </summary>
        public IReadOnlyCollection<Scope> LiveCases => Array.Empty<Scope>();

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
        /// Gets the rules of an iteration in <paramref name="scope"/> (§6.5.2): the rulesets' global rules (snapshots, in
        /// definition order), then the local rules belonging to those rulesets of each scope from the root down to
        /// <paramref name="scope"/>, deduplicated by reference. Mirror copy rules are never among them; they run only in
        /// syncs (D48). Recomputed for every iteration, so rules defined meanwhile join the next one (D6). A single ruleset
        /// on a path without local rules returns its snapshot, allocation-free.
        /// </summary>
        /// <param name="rulesets">The rulesets of the step.</param>
        /// <param name="scope">The scope the iteration runs in.</param>
        /// <returns>The rules.</returns>
        public static IReadOnlyList<CompiledRule> RulesFor(IReadOnlyList<Ruleset> rulesets, Scope scope)
        {
            var anyLocal = false;
            for (var up = scope; up is not null && anyLocal == false; up = up.Parent)
                anyLocal = up.LocalCompiled.Length > 0;

            if (rulesets.Count == 1 && anyLocal == false)
                return rulesets[0].Compiled;

            var seen = new HashSet<CompiledRule>(ReferenceEqualityComparer.Instance);
            var rules = new List<CompiledRule>();
            foreach (var ruleset in rulesets)
                foreach (var rule in ruleset.Compiled)
                    if (seen.Add(rule))
                        rules.Add(rule);

            if (anyLocal)
            {
                var path = new List<Scope>();
                for (var up = scope; up is not null; up = up.Parent)
                    path.Add(up);

                for (var k = path.Count - 1; k >= 0; k--)
                {
                    foreach (var rule in path[k].LocalCompiled)
                    {
                        var member = false;
                        foreach (var ruleset in rulesets)
                            member |= ReferenceEquals(rule.Rule.Ruleset, ruleset);

                        if (member && seen.Add(rule))
                            rules.Add(rule);
                    }
                }
            }

            return rules;
        }

        /// <summary>
        /// Checks, before an iteration, cancellation and then the limits (D42): the iteration limit, the time limit (the
        /// run's clock against <see cref="RunOptions.TimeLimit"/>), and the row limit (the own rows of the top-level scope
        /// and of the run's live cases against <see cref="RunOptions.RowLimit"/>).
        /// </summary>
        /// <param name="scope">The scope about to iterate.</param>
        /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
        /// <exception cref="ScheduleStopException">A limit was reached.</exception>
        public void BeforeStep(Scope scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Statistics.Iterations >= Options.IterationLimit)
                throw new ScheduleStopException(RunStopReason.IterationLimit);

            if (Options.TimeLimit is { } time && Clock.Elapsed > time)
                throw new ScheduleStopException(RunStopReason.TimeLimit);

            if (Options.RowLimit is { } limit)
            {
                var rows = Top.Engine.OwnRowCount;
                foreach (var live in LiveCases)
                    rows += live.Engine.OwnRowCount;

                if (rows > limit)
                    throw new ScheduleStopException(RunStopReason.RowLimit);
            }
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
            rules = Resolvable(scope, rules);
            if (rules.Count == 0)
                return false;

            var engine = scope.Engine;
            engine.EnsureClean();
            SyncMirrors(scope, rules);
            var started = Stopwatch.GetTimestamp();
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
                throw scope.FaultWith(panic);
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            Statistics.Iterations++;
            var report = iteration.Report;
            List<RuleStatistics>? matched = wantsSteps ? new List<RuleStatistics>() : null;
            foreach (var variant in lowered)
            {
                if (variant.Rule is not { } rule)
                    continue;

                if (Statistics.Rules.TryGetValue(rule, out var builder) == false)
                    Statistics.Rules.Add(rule, builder = new RuleStatisticsBuilder());

                var matches = report.NumMatches(variant.Description);
                var searchTime = report.RuleSearchAndApplyTime(variant.Description);
                var plan = PlanOf(report, variant.Description);
                builder.Add(matches, searchTime, plan);
                if (matched is not null && matches > 0)
                    matched.Add(new RuleStatistics(rule, matches, searchTime, 1, plan));
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

            RecordStep(RunStepKind.Iteration, scope, changed, elapsed, matched);
            return changed;
        }

        /// <summary>
        /// Leaves out of an iteration the rules that read a tag <paramref name="scope"/> cannot resolve. <c>Run</c> refuses
        /// such a rule before any work when the schedule reaches it at the start (§6.5.3); one defined on another thread
        /// during the run (D6) cannot be refused then, and failing the run half-way would leave its earlier iterations'
        /// writes behind and lose its report, so it takes part only in iterations of scopes that resolve its tags. Returns
        /// <paramref name="rules"/> itself, allocation-free, when every rule's tags resolve.
        /// </summary>
        /// <param name="scope">The scope the iteration runs in.</param>
        /// <param name="rules">The iteration's rules.</param>
        /// <returns>The rules whose tags resolve from <paramref name="scope"/>.</returns>
        static IReadOnlyList<CompiledRule> Resolvable(Scope scope, IReadOnlyList<CompiledRule> rules)
        {
            List<CompiledRule>? kept = null;
            for (var i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                var resolves = true;
                foreach (var tag in rule.Program.ReadScopes)
                    resolves &= ScopeResolver.TryResolve(scope, tag) is not null;

                if (resolves == false && kept is null)
                {
                    kept = new List<CompiledRule>(rules.Count);
                    for (var j = 0; j < i; j++)
                        kept.Add(rules[j]);
                }
                else if (resolves && kept is not null)
                {
                    kept.Add(rule);
                }
            }

            return kept ?? rules;
        }

        /// <summary>
        /// Brings current, just before an iteration, the mirrors its rules read (D48, §6.8): for each tag a rule reads,
        /// the tagged scope's copy rules run once if it is writable and changed since its last sync. Allocates only when a
        /// rule reads a scope.
        /// </summary>
        /// <param name="scope">The scope the iteration runs in.</param>
        /// <param name="rules">The iteration's rules.</param>
        void SyncMirrors(Scope scope, IReadOnlyList<CompiledRule> rules)
        {
            List<ScopeTag>? tags = null;
            foreach (var rule in rules)
                foreach (var tag in rule.Program.ReadScopes)
                    if ((tags ??= new List<ScopeTag>()).Contains(tag) == false)
                        tags.Add(tag);

            if (tags is not null)
                MirrorSync.Ensure(scope, tags, "A rule of the run", this);
        }

        /// <summary>
        /// Records a step (§6.11): keeps it with <see cref="RunOptions.RecordSteps"/>, and hands it to
        /// <see cref="RunOptions.Observer"/> on the run's thread, with the scope's guard marked so that the observer may
        /// read the run's scopes and nothing else. Every step takes an index, recorded or not.
        /// </summary>
        /// <param name="kind">What the step was.</param>
        /// <param name="scope">The scope it ran in.</param>
        /// <param name="changed">Whether it changed the scope.</param>
        /// <param name="elapsed">How long it took.</param>
        /// <param name="rules">For an iteration, the rules that matched in it; <c>null</c> when no step is wanted.</param>
        public void RecordStep(RunStepKind kind, Scope scope, bool changed, TimeSpan elapsed, IReadOnlyList<RuleStatistics>? rules)
        {
            var index = Statistics.StepCount++;
            if (wantsSteps == false)
                return;

            var step = new RunStep(kind, scope, index, changed, elapsed, rules ?? Array.Empty<RuleStatistics>());
            Statistics.Steps?.Add(step);
            if (Options.Observer is not { } observer)
                return;

            var guard = scope.Guard;
            var outer = guard.InObserver;
            guard.InObserver = true;
            try
            {
                observer(step);
            }
            finally
            {
                guard.InObserver = outer;
            }
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
            IReadOnlyList<RunStep> steps = Statistics.Steps is { } recorded ? recorded.AsReadOnly() : Array.Empty<RunStep>();
            return new RunReport(Statistics.Iterations, Statistics.TopChanged, saturated, reason, clock.Elapsed,
                new ReadOnlyDictionary<Rule, RuleStatistics>(rules), Statistics.SearchTime, Statistics.MergeTime, Statistics.RebuildTime, steps);
        }

        /// <summary>
        /// Gets the plan of a variant from an iteration's report: the first plan its description's reports carry, which the
        /// engine records only at its plan level (<see cref="EGraphOptions.RecordPlans"/>).
        /// </summary>
        static string? PlanOf(Relations.RuleSetReport report, string description)
        {
            if (report.RuleReports.TryGetValue(description, out var reports))
                foreach (var rule in reports)
                    if (rule.Plan is { } plan)
                        return plan;

            return null;
        }

    }

}
