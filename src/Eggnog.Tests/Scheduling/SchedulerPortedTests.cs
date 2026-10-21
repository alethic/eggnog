using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scheduling
{

    /// <summary>
    /// egglog's scheduler tests, ported (<c>src/scheduler.rs</c> at 5e655cae, <c>test_first_n_scheduler</c> and
    /// <c>test_scheduler_progress_is_separate_from_database_progress</c>): a step of
    /// <see cref="Schedule.RunWith"/> is egglog's <c>step_rules_with_scheduler</c>, and its report's
    /// <see cref="RunReport.CanStop"/> egglog's <c>can_stop</c>.
    /// </summary>
    public sealed class SchedulerPortedTests
    {

        /// <summary>
        /// test_first_n_scheduler: a scheduler that fires the first 10 of 101 matches a step fires 10 a step, then the
        /// last one, and can stop at the twelfth step.
        /// </summary>
        [Fact]
        public void FirstNScheduler()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<long>("R");
            var main = graph.DefineRuleset("main");
            main.DefineRule("grow", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Where(Primitives.Less.Of(x, 100L));
                rule.Then(r.Of(Primitives.Add.Of(x, 1L)));
            });
            graph.Root.Assume(r.Of(0L));
            graph.Root.Run(Schedule.Saturate(main));

            var test = graph.DefineRuleset("test");
            var s = graph.DefineRelation<long>("S");
            var testRule = test.DefineRule("test-rule", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Then(s.Of(x));
            });
            Assert.Equal(101, graph.Root.CountRows(r));

            var scheduler = graph.AddScheduler(new FirstNScheduler(10));
            var iter = 0;
            while (true)
            {
                var report = graph.Root.Run(Schedule.RunWith(scheduler, test));
                iter++;
                Assert.Equal(Math.Min(iter * 10, 101), graph.Root.CountRows(s));

                var expectedMatches = iter <= 10 ? 10 : 12 - iter;
                var only = Assert.Single(report.Rules);
                Assert.Same(testRule, only.Key);
                Assert.Equal(expectedMatches, only.Value.Matches);

                if (report.CanStop)
                    break;
            }

            Assert.Equal(12, iter);
        }

        /// <summary>
        /// test_scheduler_progress_is_separate_from_database_progress: a step that changes nothing still cannot stop while
        /// its scheduler says so.
        /// </summary>
        [Fact]
        public void SchedulerProgressIsSeparateFromDatabaseProgress()
        {
            var graph = new EGraph();
            var test = graph.DefineRuleset("test");
            var r = graph.DefineRelation<long>("R");
            test.DefineRule("noop", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Then(r.Of(x));
            });
            graph.Root.Assume(r.Of(1L), r.Of(2L), r.Of(3L), r.Of(4L));
            var scheduler = graph.AddScheduler(new DelayStopScheduler());

            var before = graph.Root.CountRows(r);
            var report = graph.Root.Run(Schedule.RunWith(scheduler, test));
            var after = graph.Root.CountRows(r);

            Assert.Equal(before, after);
            Assert.False(report.Changed);
            Assert.False(report.CanStop);
        }

    }

}
