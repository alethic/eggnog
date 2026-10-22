using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;

namespace Eggnog.Relations
{

    internal sealed partial class EGraphContext
    {

        /// <summary>
        /// The longest rule list in which a repeated rule is found by scanning back; longer lists use a hash table, so a
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
        /// run has merged, a recorded failure included, before the rebuild. Then, if the run grew the union-find, the
        /// context rebuilds with its first pass at <c>T</c>; otherwise the clock advances. A stopped context runs
        /// nothing and reports <see cref="IterationReport.Stopped"/>.
        /// </para>
        /// <para>
        /// Failures. A panic (a failure an action or a merge recorded) is thrown as <see cref="PanicException"/> after the
        /// iteration's bookkeeping, its writes merged and its progress committed, as egglog sets each rule's
        /// <c>last_run_at</c> before it runs the rule set (egglog-bridge/src/lib.rs:1411-1417). A panic stops the failing
        /// binding's actions and the join, as egglog's panic triggers an early stop (egglog-bridge/src/lib.rs:1459, 1508):
        /// the batches of bindings already formed still run, and the matches the join had not reached, in the failing rule
        /// or another, are not applied; a later run does not apply a semi-naive rule's either, since its progress moved
        /// past them, but a naive rule matches in full again. An exception thrown while the rules
        /// run undoes the run (no write it staged survives, no progress, the clock unchanged); in a child it also faults
        /// the context. An exception thrown while the run's writes merge cannot be undone: at the root the rules' progress
        /// is committed, so no match is applied twice; a child is faulted.
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
        /// own, and a failure of the run itself is not reported beside it. Callers that need the two told apart take the
        /// catch-up's failure before the run, as the library does after each scope's rebuild and on entry to each case,
        /// where a catch-up's failure faults the scope, and a run's faults only a case of a split step.
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

            // The per-rule buffers of the run come from the shared pools and go back cleared, so a run allocates nothing
            // per rule for its own bookkeeping.
            var count = rules.Length;
            var infos = ArrayPool<RuleInfo>.Shared.Rent(count);
            var plans = ArrayPool<CachedPlanInfo>.Shared.Rent(count);
            var staged = ArrayPool<RuleProgress>.Shared.Rent(count);
            var listed = count > RepeatScanLimit ? RentListingTable(count) : null;
            try
            {
                return RunRulesCore(rules, infos, plans, staged, listed);
            }
            finally
            {
                Array.Clear(infos, 0, count);
                Array.Clear(plans, 0, count);
                Array.Clear(staged, 0, count);
                ArrayPool<RuleInfo>.Shared.Return(infos);
                ArrayPool<CachedPlanInfo>.Shared.Return(plans);
                ArrayPool<RuleProgress>.Shared.Return(staged);
                if (listed is not null)
                    ArrayPool<int>.Shared.Return(listed);
            }
        }

        /// <summary>
        /// The body of <see cref="RunRulesCore(ReadOnlySpan{EGraphRuleId}, bool)"/> once the context is clean, over the
        /// run's buffers: <paramref name="infos"/>, <paramref name="plans"/> and <paramref name="staged"/> hold at least one
        /// entry per listing, and <paramref name="listed"/> is the repeat table of a long list
        /// (<see cref="RentListingTable"/>) or <c>null</c>.
        /// </summary>
        IterationReport RunRulesCore(ReadOnlySpan<EGraphRuleId> rules, RuleInfo[] infos, CachedPlanInfo[] plans, RuleProgress[] staged, int[]? listed)
        {
            // Resolve and plan every rule before the epoch, so a bad id or a rejected rule changes nothing (lib.rs:1404).
            var database = context.Database;
            var count = rules.Length;
            for (var i = 0; i < count; i++)
                infos[i] = owner.GetRuleInfo(rules[i]);

            for (var i = 0; i < count; i++)
                plans[i] = infos[i].GetOrBuildCachedPlan(database);

            var timestamp = context.BeginEpoch();
            context.NoteRuleRun(timestamp);
            var unionsBefore = context.DisplacedCount;
            var hadPanic = parent is null && owner.PanicChannel.HasValue;
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
                    // twice. A child is faulted and runs nothing again.
                    Commit(rules, staged);

                    throw;
                }
            }
            finally
            {
                ruleSet?.Dispose();
            }

            // Progress is committed when the run merged, a recorded failure included, as egglog sets each rule's
            // last_run_at before it runs (lib.rs:1409-1416), before the rebuild, which is sound because every row the run
            // wrote has ts >= T.
            var failed = TakeFailure(out var message);
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
        /// Rents the repeat table of a run of <paramref name="count"/> listings from the shared pool, cleared: open
        /// addressing over a power-of-two number of slots at least twice <paramref name="count"/>, slot <c>s</c> being
        /// entries <c>2s</c> (a rule's index plus one, 0 when free) and <c>2s + 1</c> (its latest listing). The caller
        /// returns it to <see cref="ArrayPool{T}.Shared"/>.
        /// </summary>
        static int[] RentListingTable(int count)
        {
            var slots = ListingSlots(count);
            var table = ArrayPool<int>.Shared.Rent(2 * slots);
            Array.Clear(table, 0, 2 * slots);
            return table;
        }

        /// <summary>
        /// Gets the number of slots of the repeat table of a run of <paramref name="count"/> listings.
        /// </summary>
        static int ListingSlots(int count) => (int)BitOperations.RoundUpToPowerOf2((uint)(2 * count));

        /// <summary>
        /// Gets the position of the latest earlier listing of <c>rules[index]</c> in the run being built, or -1 for a
        /// rule's first listing; a rule listed again runs with the run's own timestamp when its constants agree
        /// (<see cref="RunRulesCore(ReadOnlySpan{EGraphRuleId}, bool)"/>). O(1) amortized per rule: a scan back over at
        /// most <see cref="RepeatScanLimit"/> listings, or <paramref name="listed"/> for longer lists
        /// (<see cref="RentListingTable"/> for <c>rules.Length</c>), which it records <c>index</c> in.
        /// </summary>
        static int FindEarlierListing(ReadOnlySpan<EGraphRuleId> rules, int[]? listed, int index)
        {
            var id = rules[index];
            if (listed is null)
            {
                for (var j = index - 1; j >= 0; j--)
                    if (rules[j] == id)
                        return j;

                return -1;
            }

            // Fibonacci hashing of the rule's index, as the progress map spreads it.
            var mask = ListingSlots(rules.Length) - 1;
            var key = id.Index + 1;
            var hash = (uint)id.Index * 0x9E3779B9u;
            for (var slot = (int)(hash ^ (hash >> 16)) & mask; ; slot = (slot + 1) & mask)
            {
                var k = listed[2 * slot];
                if (k == 0)
                {
                    listed[2 * slot] = key;
                    listed[2 * slot + 1] = index;
                    return -1;
                }

                if (k == key)
                {
                    var earlier = listed[2 * slot + 1];
                    listed[2 * slot + 1] = index;
                    return earlier;
                }
            }
        }

    }

}
