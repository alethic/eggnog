using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scheduling
{

    /// <summary>
    /// Schedulers at their edges: matches that agree on the head's variables, the engine's invariants in a child that
    /// continues its parent's queries, nested saturations and repeats, refusals before any work, steps with no rule,
    /// runs a limit stopped, calls into a graph from a scheduler, and the scheduled forms of released local rules.
    /// </summary>
    public sealed class SchedulerEdgeTests
    {

        /// <summary>
        /// Two body matches that agree on the variables the head uses are two matches, as egglog's
        /// <c>CollectMatches</c> appends every one: a back-off limit of 1 bans the rule.
        /// </summary>
        [Fact]
        public void MatchesThatAgreeOnTheHeadsVariablesAreEachOffered()
        {
            var graph = new EGraph();
            var e = graph.DefineRelation<long, long>("E");
            var s = graph.DefineRelation<long>("S");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("first", rule =>
            {
                var (x, y) = rule.Vars<long, long>("x", "y");
                rule.When(e.Of(x, y));
                rule.Then(s.Of(x));
            });
            graph.Root.Assume(e.Of(0L, 1L), e.Of(0L, 2L));
            var counts = new System.Collections.Generic.List<int>();
            var recording = graph.AddScheduler(new DelegateScheduler((_, matches) =>
            {
                counts.Add(matches.Count);
                return false;
            }));
            var bo = graph.AddScheduler(new BackOffScheduler(matchLimit: 1, banLength: 5));

            graph.Root.Run(Schedule.RunWith(recording, rules));
            graph.Root.Run(Schedule.RunWith(bo, rules));

            Assert.Equal(new[] { 2 }, counts);
            Assert.Equal(0, graph.Root.CountRows(s));
        }

        /// <summary>
        /// A child whose scheduler's query does not run again keeps its parent's progress as its own, older than the
        /// child, which the engine's invariants allow for it.
        /// </summary>
        [Fact]
        public void AChildContinuingItsParentsQueryKeepsTheEnginesInvariants()
        {
            var (graph, test, r, s) = Hundred();
            var scheduler = graph.AddScheduler(new FirstNScheduler(10));
            graph.Root.Run(Schedule.RunWith(scheduler, test));
            var child = graph.Root.CreateChild();

            child.Run(Schedule.RunWith(scheduler, test));
            child.Run(Schedule.RunWith(scheduler, test));

            ((ContextEngineScope)child.Engine).Context.CheckInvariants();
        }

        /// <summary>
        /// A saturation passes a hold-back on, as a repeat does: the outer saturation runs again after an inner one that
        /// held work back, as egglog-experimental's report union does.
        /// </summary>
        [Fact]
        public void ANestedSaturationPassesAHoldBackOn()
        {
            var (graph, test, r, s) = Hundred();
            var scheduler = graph.AddScheduler(new DelayStopScheduler());

            var nested = graph.Root.Run(Schedule.Saturate(Schedule.Saturate(Schedule.RunWith(scheduler, test))));

            Assert.Equal(3, nested.Iterations);
            Assert.False(nested.CanStop);
        }

        /// <summary>
        /// A repeat of a saturation that held work back runs again.
        /// </summary>
        [Fact]
        public void ARepeatOfASaturationThatHeldBackRunsAgain()
        {
            var (graph, test, r, s) = Hundred();
            var scheduler = graph.AddScheduler(new DelayStopScheduler());

            var report = graph.Root.Run(Schedule.Repeat(5, Schedule.Saturate(Schedule.RunWith(scheduler, test))));

            Assert.Equal(3, report.Iterations);
        }

        /// <summary>
        /// A local rule that cannot be scheduled, and two rules of one name, are refused before any work.
        /// </summary>
        [Fact]
        public void LocalRulesAndSharedNamesAreCheckedBeforeAnyWork()
        {
            var (graph, test, r, s) = Hundred();
            var scheduler = graph.AddScheduler(new FirstNScheduler(10));
            var flag = graph.DefineRelation("flag");
            var child = graph.Root.CreateChild();
            child.DefineRule(test, "flag", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Then(flag.Of());
            });
            var other = graph.Root.CreateChild();
            other.DefineRule(test, "test-rule", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Then(s.Of(x));
            });

            var free = Assert.Throws<ArgumentException>(() => child.Run(Schedule.Sequence(test, Schedule.RunWith(scheduler, test))));
            var shared = Assert.Throws<ArgumentException>(() => other.Run(Schedule.Sequence(test, Schedule.RunWith(scheduler, test))));

            Assert.Contains("cannot be scheduled", free.Message, StringComparison.Ordinal);
            Assert.Contains("two rules named 'test-rule'", shared.Message, StringComparison.Ordinal);
            Assert.Equal(0, child.CountRows(s));
        }

        /// <summary>
        /// A step with no rule counts as an iteration, so that a scheduler that never can stop runs into the iteration
        /// limit rather than forever; and a run a limit stopped cannot stop.
        /// </summary>
        [Fact]
        public void AStepWithNoRuleCountsAndALimitedRunCannotStop()
        {
            var graph = new EGraph();
            var empty = graph.DefineRuleset("empty");
            var never = graph.AddScheduler(new DelegateScheduler((_, _) => false, () => false));

            var report = graph.Root.Run(Schedule.Saturate(Schedule.RunWith(never, empty)), new RunOptions { IterationLimit = 5 });

            Assert.Equal(5, report.Iterations);
            Assert.Equal(RunStopReason.IterationLimit, report.StopReason);
            Assert.False(report.CanStop);
        }

        /// <summary>
        /// A scheduler cannot call into a graph: adding a scheduler from one fails the run.
        /// </summary>
        [Fact]
        public void ASchedulerCannotCallIntoAGraph()
        {
            var (graph, test, r, s) = Hundred();
            var scheduler = graph.AddScheduler(new DelegateScheduler((_, _) =>
            {
                graph.AddScheduler(new FirstNScheduler(1));
                return false;
            }));
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<RuleEvaluationException>(() => child.Run(Schedule.RunWith(scheduler, test)));

            Assert.IsType<InvalidOperationException>(e.InnerException);
        }

        /// <summary>
        /// The scheduled form of a local rule is freed with its scope, and its worklist serves the next such rule.
        /// </summary>
        [Fact]
        public void AReleasedLocalRulesScheduledFormIsFreed()
        {
            var (graph, test, r, s) = Hundred();
            var t = graph.DefineRelation<long>("T");
            var scheduler = graph.AddScheduler(new FirstNScheduler(1000));
            graph.Root.Freeze();
            var tables = graph.Engine.FunctionCount;
            for (var i = 0; i < 3; i++)
            {
                var child = graph.Root.CreateChild();
                child.DefineRule(test, "local", rule =>
                {
                    var x = rule.Var<long>("x");
                    rule.When(r.Of(x));
                    rule.Then(t.Of(x));
                });
                child.Run(Schedule.RunWith(scheduler, test));
                Assert.Equal(101, child.CountRows(t));
                Assert.Equal(2, scheduler.ScheduledRuleCount);
                child.Dispose();
                Assert.Equal(1, scheduler.ScheduledRuleCount);
            }

            Assert.Equal(tables + 2, graph.Engine.FunctionCount);
        }

        /// <summary>
        /// A graph with <c>R</c> holding 0 to 100 and a ruleset <c>test</c> whose one rule copies <c>R</c> into <c>S</c>.
        /// </summary>
        static (EGraph Graph, Ruleset Test, Relation<long> R, Relation<long> S) Hundred()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<long>("R");
            var s = graph.DefineRelation<long>("S");
            var test = graph.DefineRuleset("test");
            test.DefineRule("test-rule", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Then(s.Of(x));
            });
            for (var i = 0L; i <= 100; i++)
                graph.Root.Assume(r.Of(i));

            return (graph, test, r, s);
        }

    }

}
