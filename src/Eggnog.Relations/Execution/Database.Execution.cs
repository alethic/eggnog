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
        /// action or an external function propagates before the merge.
        /// </para>
        /// <para>
        /// The rule set's header subsets must have been evaluated against the tables as they are now: build the rule set
        /// right before running it. Requires exclusive access to the database.
        /// </para>
        /// </remarks>
        /// <param name="ruleSet">The rules.</param>
        /// <param name="reportLevel">How much to report per rule.</param>
        /// <param name="timestamp">The timestamp the run's execution states and its merge carry
        /// (<see cref="ExecutionState.Timestamp"/>, <see cref="MergeAll"/>).</param>
        /// <returns>What the run did. An empty rule set returns <see cref="RuleSetReport.Empty"/> without merging, as
        /// egglog does.</returns>
        public RuleSetReport RunRuleSet(RuleSet ruleSet, ReportLevel reportLevel = ReportLevel.TimeOnly, uint timestamp = 0)
        {
            ArgumentNullException.ThrowIfNull(ruleSet);
            if (ruleSet.IsEmpty)
                return RuleSetReport.Empty;

            var rules = ruleSet.Rules;
            var actions = new ActionInfo[ruleSet.Count];
            for (var i = 0; i < actions.Length; i++)
                actions[i] = ruleSet.GetAction(new ActionId(i));

            var counter = new MatchCounter(actions.Length);
            var reports = new RuleReport?[rules.Length];
            var start = Stopwatch.GetTimestamp();
            var root = new ExecutionState(view, timestamp);
            if (ParallelRuleSetsOverride ?? parallelism.ParallelizeDbLevelOp(totalSizeEstimate))
                RunRulesParallel(ruleSet, actions, counter, root, reportLevel, reports);
            else
                RunRulesSerial(ruleSet, actions, counter, root, reportLevel, reports);

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

            var searchAndApplyTime = Stopwatch.GetElapsedTime(start);
            var mergeStart = Stopwatch.GetTimestamp();
            var changed = MergeAll(timestamp);
            return new RuleSetReport(changed, byDescription, searchAndApplyTime, Stopwatch.GetElapsedTime(mergeStart));
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
