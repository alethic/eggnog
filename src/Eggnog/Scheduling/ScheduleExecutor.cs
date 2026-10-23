using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// Runs one top-level <see cref="Scope.Run(Schedule, RunOptions, CancellationToken)"/>: chooses the rules of each
    /// iteration, checks cancellation and limits before each iteration and split step, runs it, records its step, and
    /// accumulates the report. It also keeps one split driver per split node, the run's live cases, and the stack of split
    /// steps in progress, whose lifts it records as steps.
    /// </summary>
    internal sealed partial class ScheduleExecutor
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
        /// Gets the cases of this run that are alive, in the order they first ran, for
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
        /// Gets whether the run observer threw: its exception propagates out of the run without faulting the scope, even a
        /// <see cref="RuleEvaluationException"/> it threw itself.
        /// </summary>
        public bool ObserverFailed { get; private set; }

        /// <summary>
        /// Gets whether an iteration of the run's own scope failed as egglog's run fails (<see cref="FailRun"/>): its
        /// exception propagates out of the run without faulting the scope.
        /// </summary>
        public bool RunFailed { get; private set; }

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
        /// Gets the rules of an iteration in <paramref name="scope"/>: the rulesets' global rules (snapshots, in definition
        /// order), then the local rules belonging to those rulesets of each scope from the root down to
        /// <paramref name="scope"/>, deduplicated by reference. Mirror copy rules are never among them; they run only in
        /// syncs. Recomputed for every iteration, so rules defined meanwhile, on any thread, join the next one. A single
        /// ruleset on a path without local rules returns its snapshot, allocation-free.
        /// </summary>
        /// <remarks>
        /// The local rules are read from the nearest scope with local rules up, since the scopes below it have none.
        /// O(rules + depth).
        /// </remarks>
        /// <param name="rulesets">The rulesets of the step.</param>
        /// <param name="scope">The scope the iteration runs in.</param>
        /// <returns>The rules; callers must not modify the list.</returns>
        public static IReadOnlyList<CompiledRule> RulesFor(IReadOnlyList<Ruleset> rulesets, Scope scope)
        {
            Scope? anchor = null;
            for (var up = scope; up is not null && anchor is null; up = up.Parent)
                if (up.LocalCompiled.Length > 0)
                    anchor = up;

            if (rulesets.Count == 1 && anchor is null)
                return rulesets[0].Compiled;

            var seen = new HashSet<CompiledRule>(ReferenceEqualityComparer.Instance);
            var rules = new List<CompiledRule>();
            for (var r = 0; r < rulesets.Count; r++)
                foreach (var rule in rulesets[r].Compiled)
                    if (seen.Add(rule))
                        rules.Add(rule);

            if (anchor is not null)
            {
                var path = new List<Scope>();
                for (var up = anchor; up is not null; up = up.Parent)
                    path.Add(up);

                for (var k = path.Count - 1; k >= 0; k--)
                {
                    foreach (var rule in path[k].LocalCompiled)
                    {
                        var member = false;
                        for (var r = 0; r < rulesets.Count; r++)
                            member |= ReferenceEquals(rule.Rule.Ruleset, rulesets[r]);

                        if (member && seen.Add(rule))
                            rules.Add(rule);
                    }
                }
            }

            return rules;
        }

        /// <summary>
        /// Checks, before an iteration or a split step, cancellation and then the limits: the iteration limit, the time
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
                // The top-level scope's own rows and the live cases'; no allocation.
                var rows = Top.Engine.OwnRowCount;
                foreach (var @case in cases)
                    if (@case.Engine.IsDisposed == false)
                        rows += @case.Engine.OwnRowCount;

                if (rows > limit)
                    throw new ScheduleStopException(RunStopReason.RowLimit);
            }
        }

        /// <summary>
        /// Runs one engine iteration of <paramref name="rules"/> in <paramref name="scope"/>.
        /// </summary>
        /// <param name="scope">The scope.</param>
        /// <param name="rules">The rules.</param>
        /// <param name="inCase">Whether the scope is a case.</param>
        /// <returns>Whether the iteration changed the scope (its rules' writes or the rebuild).</returns>
        /// <exception cref="RuleEvaluationException">An evaluation failure: the run's own scope stays usable, and a case
        /// of the run is faulted (<see cref="FailRun"/>).</exception>
        public bool RunIteration(Scope scope, IReadOnlyList<CompiledRule> rules, bool inCase)
        {
            BeforeStep(scope);
            rules = Permitted(scope, Resolvable(scope, rules));
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
                throw FailRun(scope, rules, panic);
            }

            return Account(scope, rules, lowered, iteration, Stopwatch.GetElapsedTime(started));
        }

        /// <summary>
        /// Fails an iteration that recorded a failure, a primitive's or a merge's, which stopped its join and which the
        /// engine merged and rebuilt before it threw, with its rules' progress committed: as egglog's failed run, which
        /// keeps what the matches it reached wrote and after which the e-graph goes on (<c>(fail (run 1))</c>), the scope
        /// the run was called on stays usable, whether or not it is a case. Its own mirrors are reconciled first, as
        /// after any iteration (<see cref="Account"/>), for every rule of the iteration that deletes, since a failed
        /// iteration reports no matches. A case a split step of the run made is faulted, as a failure in a split's case
        /// fails the split, which then faults the scope the run was called on.
        /// </summary>
        /// <param name="scope">The scope the iteration ran in.</param>
        /// <param name="rules">The iteration's rules, whose deletions merged; <c>null</c> for an iteration that deletes
        /// nothing.</param>
        /// <param name="panic">The engine's panic.</param>
        /// <returns>The exception, to throw.</returns>
        RuleEvaluationException FailRun(Scope scope, IReadOnlyList<CompiledRule>? rules, Relations.PanicException panic)
        {
            if (ReferenceEquals(scope, Top) == false)
                return scope.FaultWith(panic);

            RunFailed = true;
            var failure = scope.FailWith(panic);
            var called = scope.Runtime.TakeCallbackDeletions();
            if ((rules is not null || called is not null) && scope.Tag is { } tag)
                ReconcileMirrors(scope, rules ?? Array.Empty<CompiledRule>(), called, tag);

            return failure;
        }

        /// <summary>
        /// Accounts for one iteration of <paramref name="rules"/> in <paramref name="scope"/>: counts it, adds each
        /// variant's matches and time to its rule's statistics (a variant of no rule adds only time), reconciles the
        /// scope's mirrors after a rule that deletes matched, sums the engine times, and records the step.
        /// </summary>
        /// <param name="scope">The scope the iteration ran in.</param>
        /// <param name="rules">The iteration's rules, whose deletions the mirrors are reconciled for.</param>
        /// <param name="lowered">The variants the iteration ran.</param>
        /// <param name="iteration">What the engine did.</param>
        /// <param name="elapsed">The iteration's wall time.</param>
        /// <returns>Whether the iteration changed the scope (its rules' writes or the rebuild).</returns>
        bool Account(Scope scope, IReadOnlyList<CompiledRule> rules, LoweredRule[] lowered, EngineIteration iteration, TimeSpan elapsed)
        {
            Statistics.Iterations++;
            var report = iteration.Report;
            List<RuleStatistics>? matched = wantsSteps ? new List<RuleStatistics>() : null;
            var statistics = Statistics.Rules;
            if (statistics.Count == 0)
                statistics.EnsureCapacity(lowered.Length);

            // A variant the engine reports nothing for (every variant of an idle rule) adds no matches, no time and no
            // plan: it only enters the run's statistics. The others are looked up once, by description.
            var reports = report.RuleReports;
            var deleted = false;
            foreach (var variant in lowered)
            {
                if (variant.Rule is not { } rule)
                    continue;

                ref var builder = ref CollectionsMarshal.GetValueRefOrAddDefault(statistics, rule, out _);
                if (reports.Count == 0 || reports.TryGetValue(variant.Description, out var variantReports) == false)
                    continue;

                var (matches, searchTime, plan) = Sum(variantReports);
                builder.Add(matches, searchTime, plan);
                if (matched is not null && matches > 0)
                    matched.Add(new RuleStatistics(rule, matches, searchTime, 1, plan));

                deleted |= matches > 0 && rule.Compiled.Program.Deletes.Length > 0;
            }

            // A callback's deletions are known only once it ran (RuleCallbackContext.Remove).
            var called = scope.Runtime.TakeCallbackDeletions();
            if ((deleted || called is not null) && scope.Tag is { } tag)
                ReconcileMirrors(scope, rules, called, tag);

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
        /// Deletes from the scope's mirrors under its own tag the rows that the iteration's rules deleted from their
        /// tables, as a host deletion does (<c>HostBatch.Deletes.cs</c>): a scoped read of the scope's own tag reads the
        /// scope's own table, so its mirror must stay equal to it. Each row of such a mirror that the table no longer
        /// holds as it is (its key gone, or written again after the deletion with another value or subsumption) goes;
        /// the next sync copies the row the table holds now, which is newer than the copy rule's watermark. Called only
        /// after an iteration in which a rule that deletes matched. O(rows of each such mirror); nothing when no such rule
        /// deletes from a table the tag mirrors. The iteration ran only rules the scope may run (<see cref="Permitted"/>).
        /// </summary>
        /// <param name="scope">The scope the iteration ran in.</param>
        /// <param name="rules">The iteration's rules.</param>
        /// <param name="called">The tables rule callbacks deleted rows of; <c>null</c> for none.</param>
        /// <param name="tag">The scope's tag.</param>
        static void ReconcileMirrors(Scope scope, IReadOnlyList<CompiledRule> rules, ICollection<TableHandle>? called, ScopeTag tag)
        {
            // Indexed, not enumerated: this runs every iteration, and an idle iteration allocates nothing here.
            List<MirrorTable>? mirrors = null;
            for (var r = 0; r < rules.Count; r++)
            {
                var deletes = rules[r].Program.Deletes;
                for (var d = 0; d < deletes.Length; d++)
                    if (scope.Graph.Mirrors.TryGet(deletes[d], tag, out var mirror) && (mirrors ??= new()).Contains(mirror) == false)
                        mirrors.Add(mirror);
            }

            if (called is not null)
                foreach (var table in called)
                    if (scope.Graph.Mirrors.TryGet(table, tag, out var mirror) && (mirrors ??= new()).Contains(mirror) == false)
                        mirrors.Add(mirror);

            if (mirrors is null)
                return;

            var engine = scope.Engine;
            var stale = new List<(MirrorTable Mirror, uint[] Key)>();
            foreach (var mirror in mirrors)
            {
                // The mirror's rows, read first: the lookups below may not run inside the visitor.
                var keyCount = mirror.Handle.KeyArray.Length;
                var rows = new List<(uint[] Key, uint Output, bool Subsumed)>();
                engine.ForEach(mirror.Handle.Function, row =>
                {
                    var key = new uint[keyCount];
                    for (var i = 0; i < keyCount; i++)
                        key[i] = row.Values[i].Rep;

                    rows.Add((key, row.Values[keyCount].Rep, row.Subsumed));
                });

                foreach (var (key, output, subsumed) in rows)
                {
                    if (engine.TryLookup(mirror.Source.Function, key, out var held, out var heldSubsumed) == false
                        || held != output || heldSubsumed != subsumed)
                        stale.Add((mirror, key));
                }
            }

            if (stale.Count == 0)
                return;

            foreach (var (mirror, key) in stale)
                engine.StageRemove(mirror.Handle.Function, key);

            try
            {
                engine.Flush();
            }
            catch (Relations.PanicException panic)
            {
                throw scope.FaultWith(panic);
            }
        }

        /// <summary>
        /// Leaves out of an iteration the rules that may not run in <paramref name="scope"/>: one that deletes where the
        /// scope may not delete (it has live descendants, such as a split's cases, or a writable parent, as a case has),
        /// and one that writes a function whose merge is not a lattice below the root. <c>Run</c> refuses such a rule
        /// before any work when the schedule reaches it at the start; one defined on another thread during the run takes
        /// part only in the iterations where it may run, as a rule whose tag does not resolve does
        /// (<see cref="Resolvable"/>). Returns <paramref name="rules"/> itself, allocation-free, when every rule may run.
        /// </summary>
        /// <param name="scope">The scope the iteration runs in.</param>
        /// <param name="rules">The iteration's rules.</param>
        /// <returns>The rules that may run in <paramref name="scope"/>.</returns>
        static IReadOnlyList<CompiledRule> Permitted(Scope scope, IReadOnlyList<CompiledRule> rules)
        {
            bool? canDelete = null;
            List<CompiledRule>? kept = null;
            for (var i = 0; i < rules.Count; i++)
            {
                var program = rules[i].Program;
                var refused = ((program.RootOnlyWrite is not null || program.WritePrimitive is not null) && scope.IsRoot == false)
                    || (program.Deletes.Length > 0 && (canDelete ??= scope.Engine.CanDelete) == false);
                if (refused)
                {
                    if (kept is null)
                    {
                        kept = new List<CompiledRule>(rules.Count);
                        for (var j = 0; j < i; j++)
                            kept.Add(rules[j]);
                    }
                }
                else
                {
                    kept?.Add(rules[i]);
                }
            }

            return kept ?? rules;
        }

        /// <summary>
        /// Leaves out of an iteration the rules that read a tag <paramref name="scope"/> cannot resolve. <c>Run</c>
        /// refuses such a rule before any work when the schedule reaches it at the start; one defined on another thread
        /// during the run cannot be refused then, and failing the run half-way would leave its earlier iterations'
        /// writes behind and lose its report, so it takes part only in iterations of scopes that resolve its tags.
        /// Returns <paramref name="rules"/> itself, allocation-free, when every rule's tags resolve.
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
        /// Brings current, just before an iteration, the mirrors its rules read: for each tag a rule reads, the tagged
        /// scope's copy rules run once if it is writable and changed since its last sync. Allocates only when a rule
        /// reads a scope.
        /// </summary>
        /// <param name="scope">The scope the iteration runs in.</param>
        /// <param name="rules">The iteration's rules.</param>
        void SyncMirrors(Scope scope, IReadOnlyList<CompiledRule> rules)
        {
            List<ScopeTag>? tags = null;
            for (var i = 0; i < rules.Count; i++)
                foreach (var tag in rules[i].Program.ReadScopes)
                    if ((tags ??= new List<ScopeTag>()).Contains(tag) == false)
                        tags.Add(tag);

            if (tags is not null)
                MirrorSync.Ensure(scope, tags, "A rule of the run", this);
        }

        /// <summary>
        /// Records a step: keeps it with <see cref="RunOptions.RecordSteps"/>, and hands it to
        /// <see cref="RunOptions.Observer"/> on the run's thread, with the scope's guard marked so that the observer may
        /// read the run's scopes and nothing else. Every step takes an index, recorded or not.
        /// </summary>
        /// <param name="kind">What the step was.</param>
        /// <param name="scope">The scope it ran in.</param>
        /// <param name="changed">Whether it changed the scope.</param>
        /// <param name="elapsed">How long it took.</param>
        /// <param name="rules">For an iteration, the rules that matched in it; <c>null</c> when no step is wanted.</param>
        /// <param name="casesOpened">For a split step, the cases it opened.</param>
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
        /// Gets the run's driver of a split node, made on first use and kept for the run: its sources are the node's rules'
        /// sources at their positions (<see cref="SplitRule.SourceAt"/>), its in-case schedule a
        /// <see cref="ScheduleCaseRunner"/> of the node's, its lift spec the graph's, with no demand (the rules' bodies
        /// decide which keys split), and the run's limits. Cases are made with <c>IsCase</c>, <c>StopOnContradiction</c>,
        /// and the graph's <see cref="EGraph.InheritWatermarks"/>.
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
        /// Brings current, just before a split step, the mirrors its rules' matchers read, recording a
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
        /// Begins a split step: pushes its frame, from which its lifts are recorded as steps.
        /// </summary>
        /// <param name="scope">The scope split.</param>
        /// <param name="report">The step's report, which the driver fills.</param>
        public void BeginSplitStep(Scope scope, Relations.SplitReport report) =>
            frames.Add(new SplitStepFrame(scope, report, scope.Version, Stopwatch.GetTimestamp()));

        /// <summary>
        /// Records, as <see cref="RunStepKind.Lift"/> steps of the split scope, the lifts the current split step made since
        /// it last ran a case: the driver lifts between case runs. Each takes its share of the time since then, and
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
        /// Registers a case of the run, once, among its live cases, whose rows count towards
        /// <see cref="RunOptions.RowLimit"/>.
        /// </summary>
        /// <param name="case">The case.</param>
        public void AddCase(Scope @case)
        {
            if (caseSet.Add(@case))
                cases.Add(@case);
        }

        /// <summary>
        /// Takes, when a split step in <paramref name="scope"/> ends, the first failure a case of the run under it
        /// recorded in a catch-up the engine made inside the step and no later iteration of the case took
        /// (<see cref="Scope.TakeCaseCatchUpFailure"/>). The case is faulted; the caller throws the exception, and the
        /// run faults its scope with the same cause. Cost: one volatile read per case of <paramref name="scope"/>.
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
            var rules = new Dictionary<Rule, RuleStatistics>(Statistics.Rules.Count, ReferenceEqualityComparer.Instance);
            foreach (var (rule, builder) in Statistics.Rules)
                rules.Add(rule, builder.Build(rule));

            var saturated = reason == RunStopReason.Completed && Statistics.LastTopStepChanged == false;
            IReadOnlyList<RunStep> steps = Statistics.Steps is { } recorded ? recorded.AsReadOnly() : Array.Empty<RunStep>();
            return new RunReport(Statistics.Iterations, Statistics.TopChanged, saturated, reason, clock.Elapsed,
                new ReadOnlyDictionary<Rule, RuleStatistics>(rules), Statistics.SearchTime, Statistics.MergeTime, Statistics.RebuildTime, steps,
                Statistics.CasesOpened, Statistics.SplitsSkipped, Statistics.Lifts, Statistics.LiftRowsSkipped, Statistics.HeldBack);
        }

        /// <summary>
        /// Sums a variant's reports from an iteration: its matches and its search and apply time over every instance
        /// of it the iteration ran, and the first plan they carry, which the engine records only at its plan level
        /// (<see cref="EGraphOptions.RecordPlans"/>).
        /// </summary>
        static (long Matches, TimeSpan SearchTime, string? Plan) Sum(List<Relations.RuleReport> reports)
        {
            long matches = 0;
            var searchTime = TimeSpan.Zero;
            string? plan = null;
            foreach (var report in reports)
            {
                matches += report.NumMatches;
                searchTime += report.SearchAndApplyTime;
                plan ??= report.Plan;
            }

            return (matches, searchTime, plan);
        }

    }

}
