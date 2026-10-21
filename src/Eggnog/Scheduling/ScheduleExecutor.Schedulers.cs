using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Eggnog
{

    internal sealed partial class ScheduleExecutor
    {

        /// <summary>
        /// Gets or sets whether a scheduler step of the current pass held work back: it changed nothing, and its scheduler
        /// could not stop (<see cref="IRuleScheduler.CanStop"/>). A <see cref="RepeatSchedule"/> or
        /// <see cref="SaturateSchedule"/> reads and resets it for each pass and passes it on; egglog's <c>can_stop</c> is
        /// its negation for a pass that changed nothing.
        /// </summary>
        public bool HeldBack { get; set; }

        /// <summary>
        /// Runs one step of <paramref name="ruleset"/> under <paramref name="scheduler"/> in <paramref name="scope"/>:
        /// egglog's <c>step_rules_with_scheduler</c> (<c>src/scheduler.rs</c>). The queries of the rules whose scheduler
        /// asked for new matches run in one engine iteration; the scheduler chooses among each rule's matches, those left
        /// first; the chosen ones go to the rules' worklists; and every rule's action rule runs in a second iteration.
        /// One step counts as one iteration, one with no rule too.
        /// </summary>
        /// <param name="scope">The scope.</param>
        /// <param name="scheduler">The scheduler.</param>
        /// <param name="ruleset">The ruleset.</param>
        /// <param name="inCase">Whether the scope is a case.</param>
        /// <returns>Whether the step changed the scope: a head's writes, or the rebuild.</returns>
        /// <exception cref="RuleEvaluationException">An evaluation failure, or the scheduler threw; the scope is
        /// faulted.</exception>
        /// <exception cref="InvalidOperationException">The scheduler was removed.</exception>
        public bool RunSchedulerStep(Scope scope, RuleScheduler scheduler, Ruleset ruleset, bool inCase)
        {
            BeforeStep(scope);
            if (scheduler.IsRemoved)
                throw new InvalidOperationException($"Scheduler {scheduler.Name} was removed.");

            // A rule defined during the run that cannot be scheduled takes no part, as a rule the scope may not run
            // takes none; Run refuses one that is defined before it starts.
            var rules = Schedulable(Permitted(scope, Resolvable(scope, RulesFor([ruleset], scope))));
            var engine = scope.Engine;
            engine.EnsureClean();
            SyncMirrors(scope, rules);
            var started = Stopwatch.GetTimestamp();
            var state = scope.TakeSchedulerState(scheduler);
            var names = new string[rules.Count];
            var scheduled = new ScheduledRule[rules.Count];
            for (var i = 0; i < rules.Count; i++)
            {
                names[i] = rules[i].Rule.Name;
                scheduled[i] = scheduler.Schedule(rules[i]);
            }

            // A state taken from an ancestor continues the ancestor's queries: their progress becomes this scope's own.
            if (state.Seeded == false)
            {
                foreach (var (rule, held) in state.Rules)
                    if (held.Progress is { } progress)
                        engine.SetProgress(Query(engine, rule).Id, progress);

                state.Seeded = true;
            }

            if (rules.Count == 0)
            {
                Statistics.Iterations++;
                RecordStep(RunStepKind.Iteration, scope, false, Stopwatch.GetElapsedTime(started), null);
                HoldBackUnlessStopping(scope, scheduler, state, names, ruleset);
                return false;
            }

            var queries = new LoweredRule[rules.Count];
            var buffers = new Dictionary<int, SchedulerMatchBuffer>();
            var queried = RunQueries(scope, state, scheduled, queries, buffers);
            if (Choose(scope, scheduler, state, ruleset, names, scheduled, queries, buffers) > 0)
            {
                try
                {
                    engine.Flush();
                }
                catch (Relations.PanicException panic)
                {
                    throw scope.FaultWith(panic);
                }
            }

            // Every rule's action rule runs, as egglog's does, whether or not its worklist has rows.
            var actions = new LoweredRule[rules.Count];
            var ids = new Relations.EGraphRuleId[rules.Count];
            for (var i = 0; i < actions.Length; i++)
            {
                actions[i] = engine.PrepareVariant(scheduled[i].Action, scheduled[i].Compiled.Program.WrittenIdConstants, false);
                ids[i] = actions[i].Id;
            }

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

            var changed = Account(scope, rules, actions, iteration, Stopwatch.GetElapsedTime(started));
            if (queried is not null)
                AccountQueries(rules, queries, queried);

            if (changed == false)
                HoldBackUnlessStopping(scope, scheduler, state, names, ruleset);

            return changed;
        }

        /// <summary>
        /// Leaves out the rules that cannot be scheduled: those whose head uses no variable of their body. Returns
        /// <paramref name="rules"/> itself, allocation-free, when every rule can be.
        /// </summary>
        static IReadOnlyList<CompiledRule> Schedulable(IReadOnlyList<CompiledRule> rules)
        {
            List<CompiledRule>? kept = null;
            for (var i = 0; i < rules.Count; i++)
            {
                if (ScheduledRule.HasFreeVariables(rules[i].Program))
                {
                    kept?.Add(rules[i]);
                }
                else if (kept is null)
                {
                    kept = new List<CompiledRule>(rules.Count);
                    for (var j = 0; j < i; j++)
                        kept.Add(rules[j]);
                }
            }

            return kept ?? rules;
        }

        /// <summary>
        /// Runs, in one engine iteration, the queries of the rules whose scheduler asked for new matches at the last step
        /// (all of them at the first), each into its own buffer. Their time counts in the run's totals.
        /// </summary>
        /// <returns>The iteration's report; <c>null</c> when no query ran.</returns>
        Relations.RuleSetReport? RunQueries(Scope scope, SchedulerState state, ScheduledRule[] scheduled, LoweredRule[] queries,
            Dictionary<int, SchedulerMatchBuffer> buffers)
        {
            var engine = scope.Engine;
            var ids = new List<Relations.EGraphRuleId>(scheduled.Length);
            for (var i = 0; i < scheduled.Length; i++)
            {
                queries[i] = Query(engine, scheduled[i]);
                if (Held(state, scheduled[i]).ShouldSeek)
                {
                    ids.Add(queries[i].Id);
                    buffers.Add(scheduled[i].Slot, new SchedulerMatchBuffer(scheduled[i].Types.Length));
                }
            }

            if (ids.Count == 0)
                return null;

            var runtime = scope.Runtime;
            runtime.SchedulerMatches = buffers;
            runtime.TakeFailure();
            EngineIteration iteration;
            try
            {
                iteration = engine.RunRules(ids.ToArray());
            }
            catch (Relations.PanicException panic)
            {
                throw scope.FaultWith(panic);
            }
            finally
            {
                runtime.SchedulerMatches = null;
            }

            Statistics.SearchTime += iteration.Report.SearchAndApplyTime;
            Statistics.MergeTime += iteration.Report.MergeTime;
            Statistics.RebuildTime += iteration.RebuildTime;
            return iteration.Report;
        }

        /// <summary>
        /// Adds each query's search time and plan to its rule's statistics, as egglog reports a scheduled rule's query
        /// under the rule's name; its matches do not count. After the actions', so that the rule's plan is its body's.
        /// </summary>
        void AccountQueries(IReadOnlyList<CompiledRule> rules, LoweredRule[] queries, Relations.RuleSetReport report)
        {
            var reports = report.RuleReports;
            for (var i = 0; i < rules.Count; i++)
            {
                if (reports.TryGetValue(queries[i].Description, out var variantReports) == false)
                    continue;

                var (_, searchTime, plan) = Sum(variantReports);
                CollectionsMarshal.GetValueRefOrAddDefault(Statistics.Rules, rules[i].Rule, out _).Add(0, searchTime, plan);
            }
        }

        /// <summary>
        /// Lets the scheduler choose among each rule's matches, those it left first and then those its query found, with
        /// their classes canonical; stages the chosen ones into the rule's worklist and keeps the rest, in egglog's order,
        /// with the query's progress. Returns the number of rows staged.
        /// </summary>
        /// <exception cref="RuleEvaluationException">The scheduler threw; the scope is faulted.</exception>
        int Choose(Scope scope, RuleScheduler scheduler, SchedulerState state, Ruleset ruleset, string[] names, ScheduledRule[] scheduled,
            LoweredRule[] queries, Dictionary<int, SchedulerMatchBuffer> buffers)
        {
            var engine = scope.Engine;
            var next = new ScheduledRuleState[scheduled.Length];
            var chosen = new List<uint[]>[scheduled.Length];
            for (var i = 0; i < scheduled.Length; i++)
            {
                var rule = scheduled[i];
                var held = Held(state, rule);
                var width = rule.Types.Length;
                var sought = buffers.TryGetValue(rule.Slot, out var buffer);
                var found = sought ? buffer!.Cells : ReadOnlySpan<uint>.Empty;
                var cells = new uint[held.Pending.Length + found.Length];
                held.Pending.CopyTo(cells, 0);
                found.CopyTo(cells.AsSpan(held.Pending.Length));
                var count = cells.Length / width;
                if (count > 0 && rule.ClassColumns.Length > 0)
                    engine.CanonicalizeColumns(cells, width, rule.ClassColumns);

                var matches = new RuleMatches(cells, count, rule.Variables, rule.Types, scope);
                var name = names[i];
                var seek = Call(scope, scheduler, name, () => state.Instance.FilterMatches(name, ruleset.Name, matches));
                var progress = held.Progress;
                if (sought && engine.TryGetProgress(queries[i].Id, out var ran))
                    progress = ran;

                chosen[i] = new List<uint[]>();
                next[i] = new ScheduledRuleState(Instantiate(matches, cells, width, chosen[i]), seek, progress);
            }

            var unit = scope.Graph.Unit.Unit;
            var rows = 0;
            for (var i = 0; i < scheduled.Length; i++)
            {
                state.Rules[scheduled[i]] = next[i];
                foreach (var row in chosen[i])
                {
                    var staged = new uint[row.Length + 1];
                    row.CopyTo(staged, 0);
                    staged[^1] = unit;
                    engine.StageRow(scheduled[i].Worklist.Function, staged);
                    rows++;
                }
            }

            return rows;
        }

        /// <summary>
        /// Splits the matches into the chosen rows and those left, as egglog's <c>Matches::instantiate</c> does: every
        /// match when all are chosen; otherwise the chosen ones, in the order chosen, and the rest after egglog's swap
        /// removal of the chosen ones from the end, which the next step offers in that order.
        /// </summary>
        /// <returns>The matches left.</returns>
        static uint[] Instantiate(RuleMatches matches, uint[] cells, int width, List<uint[]> rows)
        {
            var count = matches.Count;
            if (matches.AllChosen)
            {
                for (var m = 0; m < count; m++)
                    rows.Add(cells.AsSpan(m * width, width).ToArray());

                return Array.Empty<uint>();
            }

            foreach (var index in matches.Chosen)
                rows.Add(cells.AsSpan(index * width, width).ToArray());

            var distinct = new List<int>(matches.Chosen);
            distinct.Sort();
            for (var i = distinct.Count - 1; i > 0; i--)
                if (distinct[i] == distinct[i - 1])
                    distinct.RemoveAt(i);

            var left = cells.AsSpan();
            var p = count;
            for (var i = distinct.Count - 1; i >= 0; i--)
            {
                p--;
                var c = distinct[i];
                if (c != p)
                {
                    for (var k = 0; k < width; k++)
                        (left[c * width + k], left[p * width + k]) = (left[p * width + k], left[c * width + k]);
                }
            }

            return left[..(p * width)].ToArray();
        }

        /// <summary>
        /// Asks the scheduler whether it can stop after a step that changed nothing; when it cannot, the step held work
        /// back, for the enclosing <see cref="RepeatSchedule"/> or <see cref="SaturateSchedule"/> and the report.
        /// </summary>
        void HoldBackUnlessStopping(Scope scope, RuleScheduler scheduler, SchedulerState state, string[] names, Ruleset ruleset)
        {
            if (Call(scope, scheduler, null, () => state.Instance.CanStop(names, ruleset.Name)))
                return;

            HeldBack = true;
            if (ReferenceEquals(scope, Top))
                Statistics.HeldBack = true;
        }

        /// <summary>
        /// Calls the scheduler under <see cref="EngineCallMarker"/>, so that it cannot call into a graph; an exception it
        /// throws faults the scope.
        /// </summary>
        /// <exception cref="RuleEvaluationException">The scheduler threw.</exception>
        static bool Call(Scope scope, RuleScheduler scheduler, string? rule, Func<bool> call)
        {
            EngineCallMarker.Enter();
            try
            {
                return call();
            }
            catch (Exception exception)
            {
                var failure = new RuleEvaluationException(rule, null,
                    $"scheduler {scheduler.Name} threw {exception.GetType().Name}: {exception.Message}", exception);
                scope.Fault(failure);
                throw failure;
            }
            finally
            {
                EngineCallMarker.Exit();
            }
        }

        /// <summary>
        /// Gets the semi-naive query variant of a scheduled rule, lowered at its first use.
        /// </summary>
        static LoweredRule Query(EngineScope engine, ScheduledRule rule) =>
            engine.PrepareVariant(rule.Query, rule.Compiled.Program.WrittenIdConstants, true);

        /// <summary>
        /// Gets what the state holds for a rule: the initial state for one never stepped.
        /// </summary>
        static ScheduledRuleState Held(SchedulerState state, ScheduledRule rule) =>
            state.Rules.TryGetValue(rule, out var held) ? held : ScheduledRuleState.Initial;

    }

}
