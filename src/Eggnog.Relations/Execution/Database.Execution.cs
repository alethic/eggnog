using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Eggnog.Relations
{

    internal sealed partial class Database
    {

        /// <summary>
        /// The serial join of the last serial run that finished normally, kept so the next serial run reuses its node
        /// pool, per-node indexes, frame buffers and scratch (<see cref="JoinRun.Rebind"/>); <c>null</c> while a run
        /// holds it or after a run that stopped early or threw. A clone starts without one.
        /// </summary>
        JoinRun? idleSerialJoin;

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
        /// deterministic (decision 4). Parallel mode runs one task per rule, forks morsels of frames at join positions 0
        /// and 1, and runs full action batches as tasks; it produces the same facts, but fresh ids, match counts and
        /// the order of staged writes may differ.
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
        /// </remarks>
        /// <param name="ruleSet">The rules.</param>
        /// <param name="reportLevel">How much to report per rule.</param>
        /// <param name="timestamp">The timestamp the run's execution states and its merge carry
        /// (<see cref="ExecutionState.Timestamp"/>, <see cref="MergeAll"/>); <c>null</c> for the store clock's current
        /// value, or zero without a store (colored-contexts spec §4.1). Reading the store clock from an action
        /// (<see cref="ReadCounterInstr"/>) returns it, however far other contexts advance the clock meanwhile (C1).</param>
        /// <returns>What the run did. An empty rule set returns <see cref="RuleSetReport.Empty"/> without merging, as
        /// egglog does.</returns>
        /// <exception cref="NotSupportedException">This is a child context's database (colored-contexts spec §17, M9.1
        /// interim): a child's rules run against its views, which arrive with layered execution (M10.2). Until then the
        /// executor would read the child's own layers (its own union-find rows) as if they were what the child sees,
        /// or a foreign rule set's header subsets against them; the run is refused instead. One owner test per run at
        /// the root.</exception>
        public RuleSetReport RunRuleSet(RuleSet ruleSet, ReportLevel reportLevel = ReportLevel.TimeOnly, uint? timestamp = null)
        {
            ArgumentNullException.ThrowIfNull(ruleSet);
            ThrowIfFrozen();
            if (IsChild)
                throw new NotSupportedException("Rules in a child context arrive in M10.2.");

            if (ruleSet.IsEmpty)
                return RuleSetReport.Empty;

            // Resolved once, so the run and its merge carry the same timestamp however the store clock moves between.
            var resolved = ResolveTimestamp(timestamp);
            var executed = ExecuteRuleSetCore(ruleSet, reportLevel, resolved);
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
        /// tables' staged writes are as they were before the run (writes staged earlier stay). Fresh ids the run minted,
        /// values and containers it interned, and the counters it advanced stay, as nothing refers to them.
        /// </para>
        /// <para>
        /// Not in egglog (core-relations/src/free_join/execute.rs:347-636), where a panic in a primitive unwinds through
        /// the run and the dropped buffers flush into the pending queues. The port kept that: the next
        /// <see cref="MergeAll"/> applied the batches of the failed run that had finished, and a caller that ran the
        /// rules again applied their matches twice. Marking the staged writes costs O(tables × shards) per run and
        /// allocates only for the tables with writes staged before the run.
        /// </para>
        /// </remarks>
        /// <param name="ruleSet">The rules.</param>
        /// <param name="reportLevel">How much to report per rule.</param>
        /// <param name="timestamp">The timestamp the run's execution states carry; <c>null</c> for the store clock's
        /// current value, or zero without a store (colored-contexts spec §4.1).</param>
        /// <returns>The run's report, not merged yet: <see cref="RuleSetReport.Changed"/> is <c>false</c> and
        /// <see cref="RuleSetReport.MergeTime"/> zero. <see cref="RuleSetReport.Empty"/> for an empty rule set.</returns>
        /// <exception cref="NotSupportedException">This is a child context's database (see <see cref="RunRuleSet"/>).
        /// </exception>
        public RuleSetReport ExecuteRuleSet(RuleSet ruleSet, ReportLevel reportLevel = ReportLevel.TimeOnly, uint? timestamp = null)
        {
            ArgumentNullException.ThrowIfNull(ruleSet);
            if (IsChild)
                throw new NotSupportedException("Rules in a child context arrive in M10.2.");

            if (ruleSet.IsEmpty)
                return RuleSetReport.Empty;

            return ExecuteRuleSetCore(ruleSet, reportLevel, ResolveTimestamp(timestamp));
        }

        /// <summary>
        /// <see cref="ExecuteRuleSet"/> on a nonempty rule set with the timestamp resolved.
        /// </summary>
        RuleSetReport ExecuteRuleSetCore(RuleSet ruleSet, ReportLevel reportLevel, uint timestamp)
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
            try
            {
                if (ParallelRuleSetsOverride ?? parallelism.ParallelizeDbLevelOp(totalSizeEstimate))
                    RunRulesParallel(ruleSet, actions, counter, root, reportLevel, reports);
                else
                    RunRulesSerial(ruleSet, actions, counter, root, reportLevel, reports);
            }
            catch
            {
                // Every task has finished (WorkScope.Wait returns only then), so nothing flushes any more.
                DiscardStagedWritesSince(marks);
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
        public RuleSetReport MergeRuleSet(RuleSet ruleSet, RuleSetReport executed, uint? timestamp = null)
        {
            ArgumentNullException.ThrowIfNull(ruleSet);
            ArgumentNullException.ThrowIfNull(executed);
            if (ruleSet.IsEmpty)
                return executed;

            return MergeRuleSetCore(executed, ResolveTimestamp(timestamp));
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
        /// last serial run kept, if any, and is kept again unless the run stops early or throws.
        /// </summary>
        void RunRulesSerial(RuleSet ruleSet, ActionInfo[] actions, MatchCounter counter, ExecutionState root, ReportLevel reportLevel, RuleReport?[] reports)
        {
            var rules = ruleSet.Rules;
            var join = idleSerialJoin;
            idleSerialJoin = null;
            if (join is null)
                join = new JoinRun(this, root.Signals, parallel: false);
            else
                join.Rebind(root.Signals);

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

            // An early stop may leave probers and frame-owned nodes behind; only a join that ran to the end is clean.
            if (root.ShouldStop == false)
            {
                join.Park();
                idleSerialJoin = join;
            }

            buffer.FlushOnClone();
        }

        /// <summary>
        /// The parallel path: one task per rule in one scope, which also receives the rules' morsels and full action
        /// batches; the calling thread helps until all have finished (core-relations/src/free_join/execute.rs:358-499).
        /// </summary>
        void RunRulesParallel(RuleSet ruleSet, ActionInfo[] actions, MatchCounter counter, ExecutionState root, ReportLevel reportLevel, RuleReport?[] reports)
        {
            var rules = ruleSet.Rules;
            var scope = new WorkScope(parallelism.DegreeOfParallelism);
            var framePool = new FrameUpdatesPool();
            for (var i = 0; i < rules.Length; i++)
            {
                var rule = rules[i];
                var plan = reportLevel == ReportLevel.TimeOnly ? null : rule.Template.ToString();
                var buffer = new ActionBuffer(actions, counter, root, scope);
                scope.Spawn(new RuleWork(this, rule, buffer, root.Signals, framePool, plan, reports, i));
            }

            scope.Wait();
        }

    }

}
