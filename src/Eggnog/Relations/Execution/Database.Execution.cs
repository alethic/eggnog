using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class Database
    {

        /// <summary>
        /// The serial join of the last serial run that ran to the end or stopped early, kept so the next serial run reuses
        /// its node pool, per-node indexes, frame buffers and scratch (<see cref="JoinRun.Rebind"/>); <c>null</c> while a
        /// run holds it or after a run that threw. A clone starts without one.
        /// </summary>
        JoinRun? idleSerialJoin;

        /// <summary>
        /// What the executor did, when <see cref="ExecutorCounters.Enabled"/>; made on first use.
        /// </summary>
        ExecutorCounters? executorCounts;

        /// <summary>
        /// Gets the counts the database's rule runs add to, or <c>null</c> unless the process runs with
        /// <c>EGGNOG_COUNTERS</c> (<see cref="ExecutorCounters.Enabled"/>). One instance per database, so a context's
        /// counts are its own.
        /// </summary>
        internal ExecutorCounters? ExecutorCounts
        {
            get
            {
                if (ExecutorCounters.Enabled == false)
                    return null;

                var counts = Volatile.Read(ref executorCounts);
                if (counts is not null)
                    return counts;

                counts = new ExecutorCounters();
                return Interlocked.CompareExchange(ref executorCounts, counts, null) ?? counts;
            }
        }

        /// <summary>
        /// Gets or sets whether <see cref="RunRuleSet"/> takes the parallel path regardless of the database's size;
        /// <c>null</c> (the default) decides by <see cref="ParallelHeuristics.ParallelizeDbLevelOp"/>, as egglog does.
        /// A test hook: it lets small databases exercise the parallel executor, which then runs on
        /// <see cref="ParallelHeuristics.DegreeOfParallelism"/> threads (one thread when that is one).
        /// </summary>
        internal bool? ParallelRuleSetsOverride { get; set; }

        /// <summary>
        /// Runs every rule of <paramref name="ruleSet"/> against the current tables, then merges what their actions
        /// staged. egglog's <c>run_rule_set</c> (core-relations/src/free_join/execute.rs:347-636).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every rule sees the same snapshot: actions stage their writes, and the writes become visible only when
        /// <see cref="MergeAll"/> runs at the end, so an iteration is Jacobi-style. Each rule joins its atoms (root row
        /// sets narrowed by its header subsets, then the existence checks of atoms without variables) with its plan, and
        /// every match goes into a batch of <see cref="Bindings.DefaultBatchSize"/> for its action program, which runs
        /// when the batch fills and at the end. A decomposed plan first fills one materialization per block.
        /// </para>
        /// <para>
        /// Serial mode, when the database holds at most <see cref="ParallelHeuristics.DbLevelThreshold"/> rows or the
        /// degree of parallelism is one, runs the rules in rule order on one thread with one action buffer and is
        /// deterministic for a given program. Parallel mode runs one task per rule, forks morsels of frames at join
        /// positions 0 and 1, and runs full action batches as tasks; it produces the same facts, but fresh ids, match
        /// counts and the order of staged writes may differ.
        /// </para>
        /// <para>
        /// Early stop: an action that calls <see cref="ExecutionState.TriggerEarlyStop"/> stops the join at the next
        /// check; batches already formed still run, and the merge still happens. The first failure an action reports
        /// (<see cref="ExecutionState.ReportFailure"/>) is recorded in <see cref="Failure"/>. An exception thrown by an
        /// action or an external function propagates before the merge, and nothing the run staged survives it
        /// (<see cref="ExecuteRuleSet"/>); an exception thrown by a merge function propagates from <see cref="MergeAll"/>.
        /// </para>
        /// <para>
        /// The rule set's header subsets must have been evaluated against the tables as they are now: build the rule set
        /// right before running it. Requires exclusive access to the database.
        /// </para>
        /// <para>
        /// Views. The run reads every table through the views the rule set pinned when it was built on this database's
        /// <see cref="View"/> (<see cref="RuleSet.Views"/>), which a root's run reads as its single-layer tables. A
        /// rule set built on another database's view is refused: its header subsets describe other tables. In a child
        /// context the run is the context's: it enters the context's thread domain (refused from inside a write batch's
        /// action or a visitor of the domain), requires the context's views to be current
        /// (<see cref="Context.ThrowIfViewNotCurrent"/>) and unchanged since the rule set's first header was evaluated
        /// (<see cref="RuleSet.ViewGeneration"/> and <see cref="RuleSet.ViewStamp"/>, so the headers and the run read
        /// the same rows), requires the timestamp to keep the context's epochs nondecreasing and at or above its floor
        /// (<see cref="Context.UseEpoch"/>), marks the search (<see cref="ContextTables.InSearch"/>, so layers its
        /// tasks create are adopted at the merge) as a callback of the domain (writes and rebuilds of the domain are
        /// refused inside it), and faults the context when an exception escapes. A refusal happens before anything runs
        /// and does not fault. The root pays one reference compare and one null test per run.
        /// </para>
        /// </remarks>
        /// <param name="ruleSet">The rules.</param>
        /// <param name="reportLevel">How much to report per rule.</param>
        /// <param name="timestamp">The timestamp the run's execution states and its merge carry
        /// (<see cref="ExecutionState.Timestamp"/>, <see cref="MergeAll"/>); <c>null</c> for the store clock's current
        /// value, or zero without a store. Reading the store clock from an action (<see cref="ReadCounterInstr"/>)
        /// returns it, however far other contexts advance the clock meanwhile.</param>
        /// <returns>What the run did. An empty rule set returns <see cref="RuleSetReport.Empty"/> without merging, as
        /// egglog does.</returns>
        /// <exception cref="ArgumentException">The rule set was built on another database's view.</exception>
        /// <exception cref="ArgumentOutOfRangeException">In a child, the timestamp is below the context's creation, its last
        /// noted rule run or its latest epoch.</exception>
        /// <exception cref="InvalidOperationException">The database is frozen; or, in a child, the context is not writable,
        /// its views are not current or changed since the rule set's first header was evaluated, its domain is held by
        /// another thread, or the call comes from inside a write batch's action or a visitor of its domain.</exception>
        public RuleSetReport RunRuleSet(RuleSet ruleSet, ReportLevel reportLevel = ReportLevel.TimeOnly, uint? timestamp = null)
        {
            ArgumentNullException.ThrowIfNull(ruleSet);
            ThrowIfFrozen();
            var views = RequireViews(ruleSet);
            if (ruleSet.IsEmpty)
                return RuleSetReport.Empty;

            // Resolved once, so the run and its merge carry the same timestamp however the store clock moves between.
            var resolved = ResolveTimestamp(timestamp);
            if (layers is { } l)
                return RunInChild(l, ruleSet, views, reportLevel, resolved, merge: true);

            var executed = ExecuteRuleSetCore(ruleSet, views, reportLevel, resolved);
            return MergeRuleSetCore(executed, resolved);
        }

        /// <summary>
        /// Runs every rule of <paramref name="ruleSet"/> against the current tables and stages what their actions write,
        /// without merging it: the first half of <see cref="RunRuleSet"/>, which <see cref="MergeRuleSet"/> completes.
        /// Callers that must tell a failure of the rules from a failure of the merge (the bridge's rule watermarks)
        /// call the halves themselves.
        /// </summary>
        /// <remarks>
        /// <para>
        /// All or nothing: if an action or an external function throws, every write the run staged is discarded before
        /// the exception propagates, including those of the action batches that finished and flushed before it, so the
        /// tables' staged writes are as they were before the run (writes staged before it stay). Fresh ids the run
        /// minted, values and containers it interned, and the counters it advanced stay, as nothing refers to them.
        /// </para>
        /// <para>
        /// egglog does not discard them (core-relations/src/free_join/execute.rs:347-636): a panic in a primitive
        /// unwinds through the run and the dropped buffers flush into the pending queues, so the next merge applies the
        /// batches of the failed run that had finished, and a caller that runs the rules again applies their matches
        /// twice. Marking the staged writes costs O(tables × shards) per run and allocates only for the tables with
        /// writes staged before the run.
        /// </para>
        /// </remarks>
        /// <param name="ruleSet">The rules.</param>
        /// <param name="reportLevel">How much to report per rule.</param>
        /// <param name="timestamp">The timestamp the run's execution states carry; <c>null</c> for the store clock's
        /// current value, or zero without a store.</param>
        /// <returns>The run's report, not merged yet: <see cref="RuleSetReport.Changed"/> is <c>false</c> and
        /// <see cref="RuleSetReport.MergeTime"/> zero. <see cref="RuleSetReport.Empty"/> for an empty rule set.</returns>
        /// <exception cref="ArgumentException">The rule set was built on another database's view.</exception>
        /// <exception cref="ArgumentOutOfRangeException">In a child, as for <see cref="RunRuleSet"/>.</exception>
        /// <exception cref="InvalidOperationException">In a child, as for <see cref="RunRuleSet"/>.</exception>
        public RuleSetReport ExecuteRuleSet(RuleSet ruleSet, ReportLevel reportLevel = ReportLevel.TimeOnly, uint? timestamp = null)
        {
            ArgumentNullException.ThrowIfNull(ruleSet);
            var views = RequireViews(ruleSet);
            if (ruleSet.IsEmpty)
                return RuleSetReport.Empty;

            var resolved = ResolveTimestamp(timestamp);
            if (layers is { } l)
                return RunInChild(l, ruleSet, views, reportLevel, resolved, merge: false);

            return ExecuteRuleSetCore(ruleSet, views, reportLevel, resolved);
        }

        /// <summary>
        /// Gets the views a run of <paramref name="ruleSet"/> reads: the ones it pinned when it was built on this
        /// database's view, or, for a rule set assembled by hand from a root's rules, the root's views of the tables it
        /// reads, pinned by this call.
        /// </summary>
        /// <exception cref="ArgumentException">The rule set was built on another database's view, or assembled by hand for
        /// a layered database, whose runs need the views header evaluation read.</exception>
        ViewSnapshot RequireViews(RuleSet ruleSet)
        {
            if (ruleSet.View is { } built)
            {
                if (ReferenceEquals(built, Volatile.Read(ref contextView)) == false)
                    throw new ArgumentException("The rule set was built on another database's view; build it with this database's NewRuleSet.", nameof(ruleSet));

                return ruleSet.Views!;
            }

            if (LayeredView is not null)
                throw new ArgumentException("A rule set run in a layered context must be built on the context's database (NewRuleSet), so its headers and its run read the same views.", nameof(ruleSet));

            return View.Snapshot(ruleSet.Rules, ruleSet.Actions, null);
        }

        /// <summary>
        /// A run of a child context's database: see <see cref="RunRuleSet"/>. The search is a callback of the context's
        /// thread domain and runs with <see cref="ContextTables.InSearch"/> set; an exception faults the context.
        /// </summary>
        /// <remarks>
        /// A failed run: an exception escaping a child's <see cref="RunRuleSet"/> or <see cref="ExecuteRuleSet"/> (an
        /// action, an external or a merge function throwing) faults the child, the rule layer's direct
        /// <see cref="ExecuteRuleSet"/> calls included, since a hide whose copy was never merged cannot be repaired.
        /// Every write the failed search staged is discarded, in the layers it created as well (they wait on the
        /// deferred list, outside the table map that the rollback walks,
        /// <see cref="ContextTables.DiscardDeferredStaged"/>), and <see cref="MergeAll"/> and <see cref="MergeTable"/>
        /// refuse a faulted child, so nothing a failed run staged can ever merge. The discard of the created layers'
        /// writes and the refusal are child-only and cost the root nothing on its paths.
        /// </remarks>
        RuleSetReport RunInChild(ContextTables tables, RuleSet ruleSet, ViewSnapshot views, ReportLevel reportLevel, uint timestamp, bool merge)
        {
            var context = owner!;
            context.ThrowIfNotWritable();
            var domain = context.Domain;
            using var scope = domain.Enter();
            context.ThrowIfNotWritable();
            Context.ThrowIfInCallback(domain, nameof(RunRuleSet));
            context.ThrowIfViewNotCurrent();

            // The header subsets were evaluated against the views as they were at the rule set's first header. A
            // dropped view (the generation) or a merge, hide, clear or stop anywhere on the chain (the stamp: header
            // subsets name rows, which a compaction moves) since then makes them describe other rows than the snapshot
            // shows.
            var generation = View.Generation;
            if (ruleSet.ViewGeneration != generation || ruleSet.ViewStamp != context.ViewStamp)
                throw new InvalidOperationException("The context's views changed since the rule set's headers were evaluated (a merge, rebuild, clear or freeze of the context or an ancestor); build the rule set again.");

            // The run stamps every row it merges with the timestamp, so the timestamp must keep every layer's sort
            // column nondecreasing and the context's new rows at or above its floor.
            context.UseEpoch(timestamp);
            try
            {
                RuleSetReport executed;
                tables.InSearch = true;
                domain.EnterCallback();
                try
                {
                    executed = ExecuteRuleSetCore(ruleSet, views, reportLevel, timestamp);
                }
                finally
                {
                    domain.ExitCallback();
                    tables.InSearch = false;
                }

                // Nothing drops a view while the search runs, layer creation included, so the headers and the run read
                // the same views.
                Debug.Assert(View.Generation == generation, "a context's views changed during a rule search");
                return merge ? MergeRuleSetCore(executed, timestamp) : executed;
            }
            catch
            {
                context.Fault();
                throw;
            }
        }

        /// <summary>
        /// <see cref="ExecuteRuleSet"/> on a nonempty rule set with the timestamp resolved, reading
        /// <paramref name="views"/>.
        /// </summary>
        RuleSetReport ExecuteRuleSetCore(RuleSet ruleSet, ViewSnapshot views, ReportLevel reportLevel, uint timestamp)
        {
            var rules = ruleSet.Rules;
            var actions = new ActionInfo[ruleSet.Count];
            for (var i = 0; i < actions.Length; i++)
                actions[i] = ruleSet.GetAction(new ActionId(i));

            var counter = new MatchCounter(actions.Length);
            var reports = new RuleReport?[rules.Length];
            var start = Stopwatch.GetTimestamp();
            var marks = MarkStagedWrites();
            var root = new ExecutionState(view, timestamp);
            root.AttachViews(views);
            try
            {
                if (ParallelRuleSetsOverride ?? parallelism.ParallelizeDbLevelOp(totalSizeEstimate))
                    RunRulesParallel(ruleSet, views, actions, counter, root, reportLevel, reports);
                else
                    RunRulesSerial(ruleSet, views, actions, counter, root, reportLevel, reports);
            }
            catch
            {
                // Every task has finished (WorkScope.Wait returns only then), so nothing flushes any more. In a child the
                // layers the search created wait on the deferred list, outside the table map: their writes are dropped
                // too, so nothing the failed run staged can ever merge. One null test at the root, on the exception
                // path only.
                DiscardStagedWritesSince(marks);
                layers?.DiscardDeferredStaged();
                throw;
            }

            RecordFailure(root);

            // Rules and actions are in bijection, so each rule's count is its action's (execute.rs:609-622).
            var byDescription = new Dictionary<string, List<RuleReport>>();
            for (var i = 0; i < rules.Length; i++)
            {
                var rule = rules[i];
                var report = reports[i]!;
                if (byDescription.TryGetValue(rule.Description, out var list) == false)
                    byDescription.Add(rule.Description, list = new List<RuleReport>());

                list.Add(new RuleReport(report.Plan, report.SearchAndApplyTime, counter.Read(rule.Action.Index)));
            }

            return new RuleSetReport(false, byDescription, Stopwatch.GetElapsedTime(start), TimeSpan.Zero);
        }

        /// <summary>
        /// Merges what <see cref="ExecuteRuleSet"/> staged for <paramref name="ruleSet"/> and completes its report: the
        /// second half of <see cref="RunRuleSet"/>. An empty rule set merges nothing, as egglog's <c>run_rule_set</c>
        /// returns before its <c>merge_all</c>.
        /// </summary>
        /// <param name="ruleSet">The rules that were executed.</param>
        /// <param name="executed">The report <see cref="ExecuteRuleSet"/> returned for them.</param>
        /// <param name="timestamp">The timestamp the run carried, which the merge states carry
        /// (<see cref="MergeAll"/>); <c>null</c> for the store clock's current value, or zero without a store.</param>
        /// <returns>The report, with whether the merge changed the database and how long it took.</returns>
        /// <remarks>
        /// In a child context the merge enters the context's thread domain, refuses a timestamp below the context's floor
        /// (<see cref="Context.UseEpoch"/>, before anything is merged) and faults the context when an exception
        /// escapes, as <see cref="RunRuleSet"/> does.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">In a child, the timestamp is below the context's creation, its
        /// last noted rule run or its latest epoch.</exception>
        public RuleSetReport MergeRuleSet(RuleSet ruleSet, RuleSetReport executed, uint? timestamp = null)
        {
            ArgumentNullException.ThrowIfNull(ruleSet);
            ArgumentNullException.ThrowIfNull(executed);
            if (ruleSet.IsEmpty)
                return executed;

            var resolved = ResolveTimestamp(timestamp);
            if (IsChild == false)
                return MergeRuleSetCore(executed, resolved);

            var context = owner!;
            context.ThrowIfNotWritable();
            var domain = context.Domain;
            using var scope = domain.Enter();
            context.ThrowIfNotWritable();
            Context.ThrowIfInCallback(domain, nameof(MergeRuleSet));
            context.UseEpoch(resolved);
            try
            {
                return MergeRuleSetCore(executed, resolved);
            }
            catch
            {
                context.Fault();
                throw;
            }
        }

        /// <summary>
        /// <see cref="MergeRuleSet"/> on a nonempty rule set with the timestamp resolved.
        /// </summary>
        RuleSetReport MergeRuleSetCore(RuleSetReport executed, uint timestamp)
        {
            var start = Stopwatch.GetTimestamp();
            var changed = MergeAllCore(timestamp);
            return executed.WithMerge(changed, Stopwatch.GetElapsedTime(start));
        }

        /// <summary>
        /// Records what every table has staged and not merged, for <see cref="DiscardStagedWritesSince"/>
        /// (<see cref="PendingState.Mark"/>). O(tables × shards).
        /// </summary>
        /// <returns>The marks of the tables with staged writes, or <c>null</c> when no table has any.</returns>
        DenseIdMap<TableId, int[]>? MarkStagedWrites()
        {
            DenseIdMap<TableId, int[]>? marks = null;
            foreach (var (id, info) in tables)
            {
                if (info.Table.Pending.Mark() is { } mark)
                    (marks ??= new DenseIdMap<TableId, int[]>()).Insert(id, mark);
            }

            return marks;
        }

        /// <summary>
        /// Discards every write staged since <paramref name="marks"/> were taken, in every table
        /// (<see cref="PendingState.DiscardSince"/>). The tables stay notified; merging a table with nothing staged
        /// changes nothing.
        /// </summary>
        /// <param name="marks">The marks <see cref="MarkStagedWrites"/> returned.</param>
        void DiscardStagedWritesSince(DenseIdMap<TableId, int[]>? marks)
        {
            foreach (var (id, info) in tables)
            {
                int[]? mark = null;
                if (marks is not null && marks.TryGetValue(id, out var found))
                    mark = found;

                info.Table.Pending.DiscardSince(mark);
            }
        }

        /// <summary>
        /// The serial path: every rule in rule order on one join with one in-place action buffer, flushed once at the
        /// end on a clone of the root state (core-relations/src/free_join/execute.rs:500-607). The join is the one the
        /// last serial run kept, if any, and is kept again unless the run throws; after an early stop its probers are
        /// released first (<see cref="JoinRun.ReleaseInterrupted"/>).
        /// </summary>
        void RunRulesSerial(RuleSet ruleSet, ViewSnapshot views, ActionInfo[] actions, MatchCounter counter, ExecutionState root, ReportLevel reportLevel, RuleReport?[] reports)
        {
            var rules = ruleSet.Rules;
            var join = idleSerialJoin;
            idleSerialJoin = null;
            if (join is null)
                join = new JoinRun(this, views, root.Signals, parallel: false);
            else
                join.Rebind(root.Signals, views);

            var buffer = new ActionBuffer(actions, counter, root, scope: null);
            for (var i = 0; i < rules.Length; i++)
            {
                var rule = rules[i];
                var plan = reportLevel == ReportLevel.TimeOnly ? null : rule.Template.ToString();
                var start = Stopwatch.GetTimestamp();

                // After an early stop every later rule would return at its first check.
                if (root.ShouldStop == false)
                    join.RunRuleSerial(rule, buffer);

                reports[i] = new RuleReport(plan, Stopwatch.GetElapsedTime(start), 0);
            }

            // An early stop leaves its probers holding what the stages it interrupted never moved back; released, the join
            // is as idle as one that ran to the end, and kept, so the run after an early stop (a query's Any, a failure)
            // does not rebuild it and its leaf-scan buffers. An exception propagates before this, and the join is dropped.
            if (root.ShouldStop)
                join.ReleaseInterrupted();

            join.Park();
            idleSerialJoin = join;
            buffer.FlushOnClone();
        }

        /// <summary>
        /// The parallel path: one task per rule in one scope, which also receives the rules' morsels and full action
        /// batches; the calling thread helps until all have finished (core-relations/src/free_join/execute.rs:358-499).
        /// </summary>
        void RunRulesParallel(RuleSet ruleSet, ViewSnapshot views, ActionInfo[] actions, MatchCounter counter, ExecutionState root, ReportLevel reportLevel, RuleReport?[] reports)
        {
            var rules = ruleSet.Rules;
            var scope = new WorkScope(parallelism.DegreeOfParallelism);
            var framePool = new FrameUpdatesPool();
            for (var i = 0; i < rules.Length; i++)
            {
                var rule = rules[i];
                var plan = reportLevel == ReportLevel.TimeOnly ? null : rule.Template.ToString();
                var buffer = new ActionBuffer(actions, counter, root, scope);
                scope.Spawn(new RuleWork(this, views, rule, buffer, root.Signals, framePool, plan, reports, i));
            }

            scope.Wait();
        }

    }

}
