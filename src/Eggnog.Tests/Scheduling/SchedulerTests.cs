using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scheduling
{

    /// <summary>
    /// Schedulers (<see cref="Schedule.RunWith"/>) beyond egglog's own tests: <c>Repeat</c> and <c>Saturate</c> over
    /// steps, a child continuing from its parent's scheduler as egglog's <c>push</c> clones it, classes canonical in the
    /// matches offered, subsumed rows offered as at 5e655cae, refusals, and bookkeeping that changes nothing.
    /// </summary>
    public sealed class SchedulerTests
    {

        /// <summary>
        /// A <c>Repeat</c> stops on egglog's <c>can_stop</c>: after a step that changed nothing and whose scheduler can
        /// stop; a run that held work back on the way cannot stop.
        /// </summary>
        [Fact]
        public void ARepeatStepsUntilTheSchedulerCanStop()
        {
            var (graph, test, _, _) = Hundred();
            var scheduler = graph.AddScheduler(new DelayStopScheduler());

            var report = graph.Root.Run(Schedule.Repeat(10, Schedule.RunWith(scheduler, test)));

            Assert.Equal(2, report.Iterations);
            Assert.False(report.Changed);
            Assert.False(report.CanStop);
        }

        /// <summary>
        /// A <c>Saturate</c> over scheduler steps stops on egglog's <c>can_stop</c>, as egglog-experimental's does: not on a
        /// step that changed nothing while its scheduler cannot stop.
        /// </summary>
        [Fact]
        public void ASaturateStopsOnceTheSchedulerCanStop()
        {
            var (graph, test, _, _) = Hundred();
            var scheduler = graph.AddScheduler(new DelayStopScheduler());

            var report = graph.Root.Run(Schedule.Saturate(Schedule.RunWith(scheduler, test)));

            Assert.Equal(2, report.Iterations);
            Assert.True(report.Saturated);
            Assert.False(report.CanStop);
        }

        /// <summary>
        /// A <c>Saturate</c> of steps fires every match, ten a step, and saturates once a step fires none.
        /// </summary>
        [Fact]
        public void ASaturateOfStepsFiresEveryMatch()
        {
            var (graph, test, s, _) = Hundred();
            var scheduler = graph.AddScheduler(new FirstNScheduler(10));

            var report = graph.Root.Run(Schedule.Saturate(Schedule.RunWith(scheduler, test)));

            Assert.Equal(101, graph.Root.CountRows(s));
            Assert.Equal(12, report.Iterations);
            Assert.Equal(101, report.Rules.Values.Single().Matches);
        }

        /// <summary>
        /// A child starts from its parent's scheduler as it was when the child was made: the matches its parent left wait
        /// in the child, which continues its parent's query rather than finding the old matches again; and the parent's
        /// state is its own.
        /// </summary>
        [Fact]
        public void AChildContinuesFromItsParentsScheduler()
        {
            var (graph, test, s, rule) = Hundred();
            var first = new FirstNScheduler(10);
            var scheduler = graph.AddScheduler(first);
            graph.Root.Run(Schedule.RunWith(scheduler, test));
            var child = graph.Root.CreateChild();

            var fired = 0L;
            var steps = 0;
            while (true)
            {
                var report = child.Run(Schedule.RunWith(scheduler, test));
                fired += report.Rules[rule].Matches;
                steps++;
                if (report.CanStop)
                    break;
            }

            Assert.Equal(91, first.Offered[1]);
            Assert.Equal(91L, fired);
            Assert.Equal(11, steps);
            Assert.Equal(101, child.CountRows(s));

            child.Dispose();
            graph.Root.Run(Schedule.RunWith(scheduler, test));
            Assert.Equal(91, first.Offered[^1]);
            Assert.Equal(20, graph.Root.CountRows(s));
        }

        /// <summary>
        /// A child made before its parent stepped starts afresh, as egglog's clone of a scheduler never stepped does,
        /// whatever its parent steps later.
        /// </summary>
        [Fact]
        public void AChildMadeBeforeItsParentStepsStartsAfresh()
        {
            var (graph, test, _, _) = Hundred();
            var first = new FirstNScheduler(10);
            var scheduler = graph.AddScheduler(first);
            var child = graph.Root.CreateChild();
            graph.Root.Run(Schedule.RunWith(scheduler, test));

            child.Run(Schedule.RunWith(scheduler, test));

            Assert.Equal(new[] { 101, 101 }, first.Offered);
        }

        /// <summary>
        /// Children of a frozen scope each continue from its scheduler, independently of each other.
        /// </summary>
        [Fact]
        public void SiblingsContinueIndependently()
        {
            var (graph, test, s, _) = Hundred();
            var first = new FirstNScheduler(10);
            var scheduler = graph.AddScheduler(first);
            graph.Root.Run(Schedule.RunWith(scheduler, test));
            graph.Root.Freeze();
            var left = graph.Root.CreateChild();
            var right = graph.Root.CreateChild();

            left.Run(Schedule.RunWith(scheduler, test));
            left.Run(Schedule.RunWith(scheduler, test));
            right.Run(Schedule.RunWith(scheduler, test));

            Assert.Equal(new[] { 101, 91, 81, 91 }, first.Offered);
            Assert.Equal(30, left.CountRows(s));
            Assert.Equal(20, right.CountRows(s));
        }

        /// <summary>
        /// The matches a scheduler left hold their classes as the scope's canonical members at the next step, where
        /// egglog's keep the ids they were found with: two matches a union made one fire once.
        /// </summary>
        [Fact]
        public void TheMatchesHoldCanonicalClasses()
        {
            var t = new NodeTheory();
            var seen = t.Graph.DefineRelation<Node>("seen");
            var test = t.Graph.DefineRuleset("test");
            test.DefineRule("see", rule =>
            {
                var n = rule.Var<Node>("n");
                rule.When(t.Tag.Of(n));
                rule.Then(seen.Of(n));
            });
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(t.Tag.Of(a), t.Tag.Of(b));
            var offered = new System.Collections.Generic.List<Node>();
            var step = 0;
            var scheduler = t.Graph.AddScheduler(new DelegateScheduler((_, matches) =>
            {
                if (step++ == 0)
                    return false;

                for (var i = 0; i < matches.Count; i++)
                    offered.Add(matches[i].Get<Node>("n"));

                matches.ChooseAll();
                return true;
            }));
            t.Root.Run(Schedule.RunWith(scheduler, test));
            t.Root.Union(a, b);

            t.Root.Run(Schedule.RunWith(scheduler, test));

            Assert.Equal(2, offered.Count);
            Assert.Equal(offered[0], offered[1]);
            Assert.True(t.Root.AreEqual(offered[0], a));
            Assert.Equal(1, t.Root.CountRows(seen));
        }

        /// <summary>
        /// As egglog at 5e655cae builds a scheduler's query, it matches subsumed rows too, where a rule's run does not.
        /// </summary>
        [Fact]
        public void AScheduledQueryMatchesSubsumedRows()
        {
            static (EGraph Graph, Ruleset Test, Relation<long> S) Subsumed()
            {
                var graph = new EGraph();
                var r = graph.DefineRelation<long>("r");
                var s = graph.DefineRelation<long>("s");
                var test = graph.DefineRuleset("test");
                test.DefineRule("copy", rule =>
                {
                    var x = rule.Var<long>("x");
                    rule.When(r.Of(x));
                    rule.Then(s.Of(x));
                });
                graph.Root.Assume(r.Of(1L), r.Of(2L));
                graph.Root.Subsume(r.Of(2L));
                return (graph, test, s);
            }

            var scheduled = Subsumed();
            var run = Subsumed();

            scheduled.Graph.Root.Run(Schedule.RunWith(scheduled.Graph.AddScheduler(new FirstNScheduler(10)), scheduled.Test));
            run.Graph.Root.Run(run.Test);

            Assert.Equal(2, scheduled.Graph.Root.CountRows(scheduled.S));
            Assert.Equal(1, run.Graph.Root.CountRows(run.S));
        }

        /// <summary>
        /// A rule whose head uses no variable of its body cannot be scheduled, where egglog at 5e655cae panics: refused
        /// before any work.
        /// </summary>
        [Fact]
        public void ARuleWhoseHeadUsesNoVariableIsRefused()
        {
            var (graph, test, _, _) = Hundred();
            var flag = graph.DefineRelation("flag");
            test.DefineRule("flag", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(graph.Tables.OfType<Relation<long>>().First().Of(x));
                rule.Then(flag.Of());
            });
            var scheduler = graph.AddScheduler(new FirstNScheduler(10));

            var e = Assert.Throws<ArgumentException>(() => graph.Root.Run(Schedule.RunWith(scheduler, test)));

            Assert.Contains("cannot be scheduled", e.Message, StringComparison.Ordinal);
            Assert.Equal(0, graph.Root.CountRows(flag));
        }

        /// <summary>
        /// A removed scheduler is refused, and removing it again does nothing.
        /// </summary>
        [Fact]
        public void ARemovedSchedulerIsRefused()
        {
            var (graph, test, _, _) = Hundred();
            var scheduler = graph.AddScheduler(new FirstNScheduler(10));
            graph.Root.Run(Schedule.RunWith(scheduler, test));

            Assert.True(graph.RemoveScheduler(scheduler));
            Assert.False(graph.RemoveScheduler(scheduler));
            Assert.True(scheduler.IsRemoved);
            Assert.Throws<ArgumentException>(() => graph.Root.Run(Schedule.RunWith(scheduler, test)));
            Assert.Throws<ArgumentException>(() => new EGraph().RemoveScheduler(scheduler));
        }

        /// <summary>
        /// A scheduler that throws fails the run with a <see cref="RuleEvaluationException"/> and faults the scope.
        /// </summary>
        [Fact]
        public void ASchedulerThatThrowsFaultsTheScope()
        {
            var (graph, test, _, _) = Hundred();
            var scheduler = graph.AddScheduler(new DelegateScheduler((_, _) => throw new InvalidOperationException("no")));
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<RuleEvaluationException>(() => child.Run(Schedule.RunWith(scheduler, test)));

            Assert.Contains("scheduler0 threw InvalidOperationException: no", e.Message, StringComparison.Ordinal);
            Assert.True(child.IsFaulted);
            Assert.False(graph.Root.IsFaulted);
        }

        /// <summary>
        /// A match holds the values of the variables the head uses, by name; another name is refused.
        /// </summary>
        [Fact]
        public void AMatchHoldsTheHeadsVariables()
        {
            var (graph, test, _, _) = Hundred();
            long smallest = -1;
            var scheduler = graph.AddScheduler(new DelegateScheduler((_, matches) =>
            {
                Assert.Equal(new[] { "x" }, matches.Variables);
                Assert.Equal(1, matches.TupleLength);
                Assert.Throws<ArgumentException>(() => matches[0].Get("y"));
                Assert.Throws<ArgumentOutOfRangeException>(() => matches.Choose(matches.Count));
                smallest = Enumerable.Range(0, matches.Count).Min(i => matches[i].Get<long>("x"));
                return false;
            }));

            graph.Root.Run(Schedule.RunWith(scheduler, test));

            Assert.Equal(0L, smallest);
        }

        /// <summary>
        /// The worklists are bookkeeping: a step whose chosen matches change nothing changes nothing, and leaves the
        /// scope's <see cref="Scope.Version"/> as it was.
        /// </summary>
        [Fact]
        public void BookkeepingChangesNothing()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<long>("R");
            var test = graph.DefineRuleset("test");
            test.DefineRule("noop", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Then(r.Of(x));
            });
            graph.Root.Assume(r.Of(1L), r.Of(2L));
            var scheduler = graph.AddScheduler(new FirstNScheduler(10));
            var version = graph.Root.Version;

            var report = graph.Root.Run(Schedule.RunWith(scheduler, test));

            Assert.False(report.Changed);
            Assert.True(report.CanStop);
            Assert.Equal(2, report.Rules.Values.Single().Matches);
            Assert.Equal(version, graph.Root.Version);
        }

        /// <summary>
        /// A step renders as egglog-experimental's <c>run-with</c>.
        /// </summary>
        [Fact]
        public void AStepRendersAsRunWith()
        {
            var (graph, test, _, _) = Hundred();
            var scheduler = graph.AddScheduler(new FirstNScheduler(10));

            Assert.Equal("(repeat 3 (run-with scheduler0 test))", Schedule.Repeat(3, Schedule.RunWith(scheduler, test)).ToString());
            Assert.Throws<ArgumentException>(() => Schedule.RunWith(scheduler, new EGraph().DefineRuleset("other")));
        }

        /// <summary>
        /// A graph with <c>R</c> holding 0 to 100 and a ruleset <c>test</c> whose one rule copies <c>R</c> into <c>S</c>.
        /// </summary>
        static (EGraph Graph, Ruleset Test, Relation<long> S, Rule Rule) Hundred()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<long>("R");
            var s = graph.DefineRelation<long>("S");
            var test = graph.DefineRuleset("test");
            var rule = test.DefineRule("test-rule", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Then(s.Of(x));
            });
            for (var i = 0L; i <= 100; i++)
                graph.Root.Assume(r.Of(i));

            return (graph, test, s, rule);
        }

    }

}
