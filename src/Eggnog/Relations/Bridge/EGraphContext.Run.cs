using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Eggnog.Relations
{

    internal sealed partial class EGraphContext
    {

        /// <summary>
        /// The longest rule list in which a repeated rule is found by scanning back; longer lists use a dictionary, so a
        /// run stays O(rules) (<see cref="FindEarlierListing"/>).
        /// </summary>
        const int RepeatScanLimit = 16;

        /// <summary>
        /// Runs the given rules once in this context, as one iteration, and rebuilds: egglog's <c>run_rules</c>
        /// (lib.rs:583-624, 1397-1420), per context.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The context is brought up to date first (<see cref="EnsureClean"/>). Each rule is planned at its first run
        /// in any context (on the statistics of the context that plans it). The run captures an epoch <c>T</c> and
        /// notes it as the context's semi-naive floor (<see cref="Context.NoteRuleRun"/>). Each rule contributes its
        /// semi-naive variants for its watermark in this context (<see cref="GetWatermark"/>: its own progress, or one
        /// seeded from an ancestor) with its Id constants in their canonical form here (K); a rule listed again runs
        /// with <c>T</c>. Every rule matches the same view; their writes, stamped <c>T</c>, merge at the end
        /// (<see cref="Database.ExecuteRuleSet"/>, <see cref="Database.MergeRuleSet"/>, which run within the context's
        /// floor, <see cref="Context.UseEpoch"/>).
        /// </para>
        /// <para>
        /// Progress: each rule's <c>(T, K, generation)</c> is staged when the rule set is built and committed once the
        /// run has merged and no failure was recorded, before the rebuild. Then, if the run grew the union-find, the
        /// context rebuilds with its first pass at <c>T</c>; otherwise the clock advances. A stopped context runs
        /// nothing and reports <see cref="IterationReport.Stopped"/>.
        /// </para>
        /// <para>
        /// Failures. A panic (a failure an action or a merge recorded) is thrown as <see cref="PanicException"/> after the
        /// iteration's bookkeeping, with no progress committed. An exception thrown while the rules run undoes the run
        /// (no write it staged survives, no progress, the clock unchanged); in a child it also faults the context. An
        /// exception thrown while the run's writes merge cannot be undone: at the root the rules' progress is committed
        /// (unless a failure was recorded), so no match is applied twice; a child is faulted.
        /// </para>
        /// <para>
        /// The report is at the e-graph's <see cref="EGraph.ReportLevel"/>.
        /// </para>
        /// </remarks>
        /// <param name="rules">The rules.</param>
        /// <returns>The report.</returns>
        /// <exception cref="ArgumentException">A rule is not registered.</exception>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is frozen or faulted; its domain is held by another
        /// thread; or the call comes from inside an operation of the same domain.</exception>
        /// <exception cref="PanicException">A failure was recorded.</exception>
        public IterationReport RunRules(params ReadOnlySpan<EGraphRuleId> rules) => RunRulesCore(rules, egglogCompatible: false);

        /// <summary>
        /// Runs <paramref name="rules"/> until they saturate: an iteration in which neither the rules nor the rebuild
        /// changed anything, a contradiction stops the context, or <paramref name="maxIterations"/> iterations ran.
        /// </summary>
        /// <param name="rules">The rules.</param>
        /// <param name="maxIterations">The most iterations to run; zero runs none.</param>
        /// <returns>The iterations run and why they ended.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxIterations"/> is negative.</exception>
        /// <exception cref="PanicException">A failure was recorded.</exception>
        public SaturateReport Saturate(ReadOnlySpan<EGraphRuleId> rules, int maxIterations)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxIterations);
            for (var i = 0; i < maxIterations; i++)
            {
                var report = RunRules(rules);
                if (report.Stopped)
                    return new SaturateReport(i + 1, true, false);

                if (report.Changed == false && report.RebuildChanged == false)
                    return new SaturateReport(i + 1, false, false);
            }

            return new SaturateReport(maxIterations, false, true);
        }

        /// <summary>
        /// Brings the context up to date: brings a parent that is not frozen up to date, flushes the host writes staged
        /// here (<see cref="FlushUpdates"/>), then rebuilds. At the root it rebuilds only when its union-find grew
        /// since its last rebuild, so writes staged at the root and not flushed stay pending into the next run, as in
        /// egglog, and the root adds no pass and no epoch at rest. Nothing for a frozen or stopped context.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted; its domain is held by another thread; or
        /// work is due and the call comes from inside an operation of the same domain.</exception>
        /// <exception cref="PanicException">The flush of host writes recorded a failure.</exception>
        public void EnsureClean()
        {
            context.ThrowIfDisposed();
            if (context.IsFrozen)
                return;

            using var scope = context.Domain.Enter();
            if (parent is { IsFrozen: false } p)
                p.EnsureClean();

            if (IsStopped)
                return;

            if (hostWrites.IsEmpty == false)
                FlushCore(throwOnFailure: true);

            if (IsRoot)
            {
                if (context.DisplacedCount != context.JournalMark)
                    context.Rebuild();
            }
            else
            {
                context.Rebuild();
            }
        }

        /// <summary>
        /// Rebuilds the context (<see cref="Context.Rebuild"/>, the one loop of every level): catches a child up with
        /// its ancestors and makes its rows canonical, or runs egglog's passes at the root. Host writes staged here are
        /// not flushed (<see cref="EnsureClean"/> does both).
        /// </summary>
        /// <returns>Whether any pass changed anything.</returns>
        public bool Rebuild() => context.Rebuild();

        /// <summary>
        /// The body of <see cref="RunRules"/> and of the e-graph's egglog-compatible <see cref="EGraph.RunRules"/>, which
        /// passes <paramref name="egglogCompatible"/>: the root then only flushes host writes staged on its bridge side and
        /// does not rebuild first, whatever was merged raw before (egglog's <c>run_rules</c> never rebuilds before the
        /// rules), and the root's panic side channel drops a panic a failed run recorded.
        /// </summary>
        /// <remarks>
        /// In a child, the catch-up before the run (<see cref="EnsureClean"/>) can record a failing lattice merge without
        /// throwing. The database keeps only the first failure, so the run then reports the catch-up's failure as its
        /// own and commits no progress, and a failure of the run itself is not reported beside it. The outcome is sound:
        /// the child is faulted either way. Callers that need the two told apart take the catch-up's failure before the
        /// run, as the library does after each scope's rebuild and on entry to each case.
        /// </remarks>
        internal IterationReport RunRulesCore(ReadOnlySpan<EGraphRuleId> rules, bool egglogCompatible)
        {
            ThrowIfUnwritable();
            var domain = context.Domain;
            using var scope = domain.Enter();
            ThrowIfUnwritable();
            Context.ThrowIfInCallback(domain, nameof(RunRules));
            if (IsStopped)
                return IterationReport.WhenStopped;

            if (egglogCompatible)
            {
                if (hostWrites.IsEmpty == false)
                    FlushCore(throwOnFailure: false);
            }
            else
            {
                EnsureClean();
            }

            if (IsStopped)
                return IterationReport.WhenStopped;

            // Resolve and plan every rule before the epoch, so a bad id or a rejected rule changes nothing (lib.rs:1404).
            var database = context.Database;
            var count = rules.Length;
            var infos = new RuleInfo[count];
            var plans = new CachedPlanInfo[count];
            for (var i = 0; i < count; i++)
                infos[i] = owner.GetRuleInfo(rules[i]);

            for (var i = 0; i < count; i++)
                plans[i] = infos[i].GetOrBuildCachedPlan(database);

            var timestamp = context.BeginEpoch();
            context.NoteRuleRun(timestamp);
            var unionsBefore = context.DisplacedCount;
            var hadPanic = parent is null && owner.PanicChannel.HasValue;
            var staged = new RuleProgress[count];
            var listed = count > RepeatScanLimit ? new Dictionary<EGraphRuleId, int>(count) : null;
            RuleSet? ruleSet = null;
            RuleSetReport report;
            try
            {
                RuleSetReport executed;
                try
                {
                    var builder = database.NewRuleSet();
                    var view = database.View;
                    for (var i = 0; i < count; i++)
                    {
                        var info = infos[i];
                        var plan = plans[i];
                        var cached = plan.Plan;
                        var constants = CanonicalConstants.Compute(view, cached.IdConstants, cached.Actions.IdConstants, cached.WrittenConstants);
                        var earlier = FindEarlierListing(rules, listed, i);
                        var watermark = earlier < 0
                            ? GetWatermark(rules[i], info, constants)
                            : RuleProgress.SameConstants(staged[earlier].Constants, constants) ? staged[earlier].LastRunAt : 0;
                        info.Query.AddRulesFromCached(builder, watermark, plan, constants);
                        staged[i] = new RuleProgress(timestamp, constants, info.Generation);
                    }

                    ruleSet = builder.Build();
                    executed = database.ExecuteRuleSet(ruleSet, owner.ReportLevel, timestamp);
                }
                catch
                {
                    // Nothing the run staged survives (Database.ExecuteRuleSet), so nothing is committed, and a panic an
                    // action recorded before the exception is dropped with the run.
                    if (hadPanic == false && parent is null)
                        owner.PanicChannel.TryTake(out _);

                    throw;
                }

                try
                {
                    report = ruleSet.IsEmpty && parent is null
                        ? MergePendingAtRoot(database, executed, timestamp)
                        : database.MergeRuleSet(ruleSet, executed, timestamp);
                }
                catch
                {
                    // A merge cannot be undone: what merged stays, so the matches are committed and never applied
                    // twice, unless a failure was recorded. A child is faulted and runs nothing again.
                    if (database.Failure is null)
                        Commit(rules, staged);

                    throw;
                }
            }
            finally
            {
                ruleSet?.Dispose();
            }

            // Progress is committed when the run merged and recorded no failure, before the rebuild, which is sound
            // because every row the run wrote has ts >= T.
            var failed = TakeFailure(out var message);
            if (failed == false)
                Commit(rules, staged);

            if (IsStopped)
            {
                context.EndEpoch();
                if (failed)
                    throw new PanicException(message);

                return new IterationReport(report, TimeSpan.Zero, false, 0, true);
            }

            if (context.DisplacedCount == unionsBefore)
            {
                // No new unions: no row can have become non-canonical; advance the clock (lib.rs:606-613).
                context.EndEpoch();
                if (failed)
                    throw new PanicException(message);

                return new IterationReport(report, TimeSpan.Zero, false, 0, false);
            }

            // The first pass reads the clock again, which gives T (lib.rs:667).
            var start = Stopwatch.GetTimestamp();
            var rebuildChanged = context.Rebuild();
            var rebuildTime = Stopwatch.GetElapsedTime(start);
            if (failed)
            {
                // A failure the rebuild records belongs to the same failed iteration.
                TakeFailure(out _);
                throw new PanicException(message);
            }

            if (TakeFailure(out var later))
                throw new PanicException(later);

            return new IterationReport(report, rebuildTime, rebuildChanged, context.LastRebuildPasses, IsStopped);
        }

        /// <summary>
        /// Merges, at the root, what is staged and not merged when every rule of the run was pruned (an empty rule set):
        /// rows and unions staged raw through <see cref="WithExecutionState{TResult}"/> or a database buffer land at the
        /// run's <c>T</c>, the epoch whose progress the run commits.
        /// </summary>
        /// <remarks>
        /// egglog's <c>run_rule_set</c> returns before its <c>merge_all</c> when the rule set is empty
        /// (core-relations/src/free_join/execute.rs:349), yet <c>run_rules</c> still records the run's timestamp as
        /// every rule's <c>last_run_at</c>. A raw row staged before such runs keeps the stamp it was staged with, so
        /// once two of them have run it lies below every rule's watermark when a later flush or run merges it, and no
        /// rule ever matches it, at the root or in any child that inherits the root's watermarks. Merging here keeps
        /// the run's watermark at or below the stamp of every row it did not see. Costs one pass over the notification
        /// list when nothing is staged; a child needs none of this, since it stamps rows at the merge and is clean
        /// before it runs.
        /// </remarks>
        static RuleSetReport MergePendingAtRoot(Database database, RuleSetReport executed, uint timestamp)
        {
            var start = Stopwatch.GetTimestamp();
            var changed = database.MergeAll(timestamp);
            return changed ? executed.WithMerge(true, Stopwatch.GetElapsedTime(start)) : executed;
        }

        /// <summary>
        /// Gets the position of the latest earlier listing of <c>rules[index]</c> in the run being built, or -1 for a
        /// rule's first listing; a rule listed again runs with the run's own timestamp when its constants agree
        /// (<see cref="RunRulesCore"/>). O(1) amortized per rule: a scan back over at most
        /// <see cref="RepeatScanLimit"/> listings, or <paramref name="listed"/> for longer lists, which it records
        /// <c>index</c> in.
        /// </summary>
        static int FindEarlierListing(ReadOnlySpan<EGraphRuleId> rules, Dictionary<EGraphRuleId, int>? listed, int index)
        {
            var id = rules[index];
            if (listed is null)
            {
                for (var j = index - 1; j >= 0; j--)
                    if (rules[j] == id)
                        return j;

                return -1;
            }

            var earlier = listed.TryGetValue(id, out var j2) ? j2 : -1;
            listed[id] = index;
            return earlier;
        }

    }

}
