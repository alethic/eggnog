using System;
using System.Diagnostics;

namespace Eggnog.Relations
{

    /// <summary>
    /// One rule of a parallel run: the task egglog's parallel <c>run_rule_set</c> spawns per plan
    /// (core-relations/src/free_join/execute.rs:376-493). It runs the rule on its own <see cref="JoinRun"/> over the
    /// rule set's pinned views and its own action buffer, flushes the batches the buffer still holds, and records the
    /// rule's report.
    /// </summary>
    internal sealed class RuleWork : ScopedWork
    {

        readonly Database db;
        readonly ViewSnapshot views;
        readonly RulePlan rule;
        readonly ActionBuffer actions;
        readonly ExecutionSignals signals;
        readonly FrameUpdatesPool framePool;
        readonly string? plan;
        readonly RuleReport?[] reports;
        readonly int index;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="db">The database.</param>
        /// <param name="views">The rule set's pinned views.</param>
        /// <param name="rule">The rule.</param>
        /// <param name="actions">The task's action buffer, with the run's scope.</param>
        /// <param name="signals">The run's stop signal.</param>
        /// <param name="framePool">The run's recycled frame buffers.</param>
        /// <param name="plan">The plan text to report, or <c>null</c>.</param>
        /// <param name="reports">Receives the report at <paramref name="index"/>.</param>
        /// <param name="index">The rule's index.</param>
        public RuleWork(Database db, ViewSnapshot views, RulePlan rule, ActionBuffer actions, ExecutionSignals signals, FrameUpdatesPool framePool, string? plan, RuleReport?[] reports, int index)
        {
            ArgumentNullException.ThrowIfNull(db);
            ArgumentNullException.ThrowIfNull(views);
            ArgumentNullException.ThrowIfNull(rule);
            ArgumentNullException.ThrowIfNull(actions);
            ArgumentNullException.ThrowIfNull(signals);
            ArgumentNullException.ThrowIfNull(framePool);
            ArgumentNullException.ThrowIfNull(reports);
            this.db = db;
            this.views = views;
            this.rule = rule;
            this.actions = actions;
            this.signals = signals;
            this.framePool = framePool;
            this.plan = plan;
            this.reports = reports;
            this.index = index;
        }

        /// <inheritdoc />
        public override void Run()
        {
            var start = Stopwatch.GetTimestamp();
            if (signals.ShouldStop == false)
                new JoinRun(db, views, signals, parallel: true, framePool).RunRuleParallel(rule, actions);

            var elapsed = Stopwatch.GetElapsedTime(start);
            if (actions.NeedsFlush)
                actions.FlushOnClone();

            reports[index] = new RuleReport(plan, elapsed, 0);
        }

    }

}
