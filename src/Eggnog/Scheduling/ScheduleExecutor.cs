using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// Runs one top-level <see cref="Scope.Run(Schedule, RunOptions, CancellationToken)"/>: chooses the rules of each
    /// iteration, checks cancellation and limits before each iteration and split step, runs it, records its step (§6.11),
    /// and accumulates the report (§6.5). From R3 it also keeps one split driver per split node, the run's live cases, and
    /// the stack of split steps in progress, whose lifts it records as steps.
    /// </summary>
    internal sealed class ScheduleExecutor
    {

        readonly CancellationToken cancellationToken;
        readonly bool wantsSteps;
        readonly List<Scope> cases = new();
        readonly HashSet<Scope> caseSet = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<SplitSchedule, Relations.SplitDriver> drivers = new(ReferenceEqualityComparer.Instance);
        readonly List<SplitStepFrame> frames = new();
        Relations.ContextOptions? caseOptions;

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
        /// Gets the cases of this run that are alive (R3), in the order they first ran, for
        /// <see cref="RunOptions.RowLimit"/>: a snapshot. A case leaves once its engine set disposed it (a vacuous case, or
        /// every case when the run ends).
        /// </summary>
        public IReadOnlyCollection<Scope> LiveCases
        {
            get
            {
                var live = new List<Scope>(cases.Count);
                foreach (var @case in cases)
                    if (@case.Engine.IsDisposed == false)
                        live.Add(@case);

                return live;
            }
        }

        /// <summary>
        /// Gets whether the run observer threw: its exception propagates out of the run without faulting the scope (§6.11),
        /// even a <see cref="RuleEvaluationException"/> it threw itself.
        /// </summary>
        public bool ObserverFailed { get; private set; }

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
        /// Checks, before an iteration or a split step, cancellation and then the limits (D42): the iteration limit, the time
        /// limit (the run's clock against <see cref="RunOptions.TimeLimit"/>), and the row limit (the own rows of the
        /// top-level scope and of the run's live cases against <see cref="RunOptions.RowLimit"/>).
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
                // The top-level scope's own rows and the live cases' (D42); no allocation.
                var rows = Top.Engine.OwnRowCount;
                foreach (var @case in cases)
                    if (@case.Engine.IsDisposed == false)
                        rows += @case.Engine.OwnRowCount;

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
        /// <param name="casesOpened">For a split step, the cases it opened (R3).</param>
        public void RecordStep(RunStepKind kind, Scope scope, bool changed, TimeSpan elapsed, IReadOnlyList<RuleStatistics>? rules, int casesOpened = 0)
        {
            var index = Statistics.StepCount++;
            if (wantsSteps == false)
                return;

            var step = new RunStep(kind, scope, index, changed, elapsed, rules ?? Array.Empty<RuleStatistics>(), casesOpened);
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
            catch
            {
                ObserverFailed = true;
                throw;
            }
            finally
            {
                guard.InObserver = outer;
            }
        }

        /// <summary>
        /// Gets the run's driver of a split node (§6.9.3), made on first use and kept for the run: its sources are the
        /// node's rules' sources at their positions (<see cref="SplitRule.SourceAt"/>), its in-case schedule a
        /// <see cref="ScheduleCaseRunner"/> of the node's, its lift spec the graph's (§6.9.1), with no demand (D43: the
        /// rules' bodies decide), and the run's limits. Cases are made with <c>IsCase</c>, <c>StopOnContradiction</c>, and
        /// the graph's <see cref="EGraph.InheritWatermarks"/>.
        /// </summary>
        /// <param name="node">The split node.</param>
        /// <returns>The driver.</returns>
        public Relations.SplitDriver DriverFor(SplitSchedule node)
        {
            if (drivers.TryGetValue(node, out var driver))
                return driver;

            var graph = Top.Graph;
            var sources = new Relations.ISplitSource[node.Rules.Length];
            for (var i = 0; i < sources.Length; i++)
                sources[i] = node.Rules[i].SourceAt(i);

            caseOptions ??= new Relations.ContextOptions
            {
                IsCase = true,
                StopOnContradiction = true,
                InheritWatermarks = graph.InheritWatermarks,
            };

            var options = new Relations.SplitOptions
            {
                DepthLimit = Options.SplitDepthLimit,
                CaseLimit = Options.CaseLimit,
                Lift = Options.Lift.Engine,
                CaseOptions = caseOptions,
            };

            driver = new Relations.SplitDriver(sources, new ScheduleCaseRunner(this, node.InCase), null, LiftSpecBuilder.Build(graph), options);
            drivers.Add(node, driver);
            return driver;
        }

        /// <summary>
        /// Brings current, just before a split step, the mirrors its rules' matchers read (D48, §6.8), recording a
        /// <see cref="RunStepKind.MirrorSync"/> step for each sync; the matcher's own sync then finds them current. Allocates
        /// only when a matcher reads a scope.
        /// </summary>
        /// <param name="scope">The scope split.</param>
        /// <param name="rules">The step's split rules.</param>
        public void SyncSplitMirrors(Scope scope, SplitRule[] rules)
        {
            List<ScopeTag>? tags = null;
            foreach (var rule in rules)
                foreach (var tag in rule.Program.Matcher.ReadScopes)
                    if ((tags ??= new List<ScopeTag>()).Contains(tag) == false)
                        tags.Add(tag);

            if (tags is not null)
                MirrorSync.Ensure(scope, tags, "A split rule of the run", this);
        }

        /// <summary>
        /// Begins a split step (R3): pushes its frame, from which its lifts are recorded as steps (§6.11).
        /// </summary>
        /// <param name="scope">The scope split.</param>
        /// <param name="report">The step's report, which the driver fills.</param>
        public void BeginSplitStep(Scope scope, Relations.SplitReport report) =>
            frames.Add(new SplitStepFrame(scope, report, scope.Version, Stopwatch.GetTimestamp()));

        /// <summary>
        /// Records, as <see cref="RunStepKind.Lift"/> steps of the split scope, the lifts the current split step made since
        /// it last ran a case (§6.11): the driver lifts between case runs. Each takes its share of the time since then, and
        /// is marked changed when the scope's <see cref="Scope.Version"/> moved meanwhile (the in-case work changes only the
        /// cases). Called when a case is about to run, and when the step ends.
        /// </summary>
        public void ObserveLifts()
        {
            if (frames.Count == 0)
                return;

            var frame = frames[^1];
            var lifts = frame.Report.Lifts - frame.LiftsSeen;
            if (lifts <= 0)
                return;

            var now = Stopwatch.GetTimestamp();
            var version = frame.Scope.Version;
            var changed = version != frame.Version;
            var share = Stopwatch.GetElapsedTime(frame.Timestamp, now) / lifts;
            frame.LiftsSeen = frame.Report.Lifts;
            frame.Version = version;
            frame.Timestamp = now;
            for (var i = 0; i < lifts; i++)
                RecordStep(RunStepKind.Lift, frame.Scope, changed, share, null);
        }

        /// <summary>
        /// Notes that a case of the current split step finished running: the window of the next lifts begins.
        /// </summary>
        public void CaseRunEnded()
        {
            if (frames.Count == 0)
                return;

            var frame = frames[^1];
            frame.Version = frame.Scope.Version;
            frame.Timestamp = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Ends the current split step: records its remaining lifts as steps, pops its frame, and adds its report to the
        /// run's statistics, also when the step threw, so that a run a limit stopped reports the cases it opened.
        /// </summary>
        public void EndSplitStep()
        {
            var frame = frames[^1];
            try
            {
                ObserveLifts();
            }
            finally
            {
                frames.RemoveAt(frames.Count - 1);
                var report = frame.Report;
                Statistics.CasesOpened += report.CasesOpened;
                Statistics.SplitsSkipped += report.SplitsSkipped;
                Statistics.Lifts += report.Lifts;
                Statistics.LiftRowsSkipped += report.LiftRowsSkipped;
            }
        }

        /// <summary>
        /// Registers a case of the run, once, among its live cases (§6.9.3), whose rows count towards
        /// <see cref="RunOptions.RowLimit"/>.
        /// </summary>
        /// <param name="case">The case.</param>
        public void AddCase(Scope @case)
        {
            if (caseSet.Add(@case))
                cases.Add(@case);
        }

        /// <summary>
        /// Takes, when a split step in <paramref name="scope"/> ends, the first failure a case of the run under it recorded
        /// in a catch-up the engine made inside the step and no later iteration of the case took (N114;
        /// <see cref="Scope.TakeCaseCatchUpFailure"/>). The case is faulted; the caller throws the exception, and the run
        /// faults its scope (N94). Cost: one volatile read per case of <paramref name="scope"/>.
        /// </summary>
        /// <param name="scope">The scope the step split.</param>
        /// <returns>The exception, to throw; <c>null</c> when nothing was recorded.</returns>
        public RuleEvaluationException? TakeCaseCatchUpFailure(Scope scope) => scope.TakeCaseCatchUpFailure(caseSet);

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
                new ReadOnlyDictionary<Rule, RuleStatistics>(rules), Statistics.SearchTime, Statistics.MergeTime, Statistics.RebuildTime, steps,
                Statistics.CasesOpened, Statistics.SplitsSkipped, Statistics.Lifts, Statistics.LiftRowsSkipped);
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
