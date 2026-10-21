using Xunit;

namespace Eggnog.Tests.Scheduling
{

    /// <summary>
    /// <see cref="BackOffScheduler"/>, egglog-experimental's back-off: its tests of the scheduler, ported
    /// (egglog-experimental 1.0.0, <c>tests/integration_test.rs</c>), where a <c>push</c> is a child and an e-graph's clone
    /// a child too; a saturation after <c>tests/repro-scheduler-scopes.egg</c>; and its bans.
    /// </summary>
    public sealed class BackOffSchedulerTests
    {

        /// <summary>
        /// test_top_level_let_scheduler_persists_on_the_egraph: a banned rule's matches wait, and the step its ban is
        /// fast-forwarded to fires them, without finding the row written meanwhile.
        /// </summary>
        [Fact]
        public void TheBannedBacklogFiresWithoutRematching()
        {
            var graph = new EGraph();
            var copy = graph.DefineRuleset("copy");
            var grow = graph.DefineRuleset("grow");
            var r = graph.DefineRelation<long>("R");
            var s = graph.DefineRelation<long>("S");
            var seed = graph.DefineRelation("Seed");
            graph.Root.Assume(r.Of(0L), r.Of(1L), r.Of(2L), seed.Of());
            copy.DefineRule("copy", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Then(s.Of(x));
            });
            grow.DefineRule("grow", rule =>
            {
                rule.When(seed.Of());
                rule.Then(r.Of(3L));
            });
            var bo = graph.AddScheduler(new BackOffScheduler(matchLimit: 2, banLength: 2));

            graph.Root.Run(Schedule.Sequence(Schedule.RunWith(bo, copy), Schedule.Run(grow), Schedule.RunWith(bo, copy)));

            Assert.Equal(3, graph.Root.CountRows(s));
        }

        /// <summary>
        /// test_top_level_let_scheduler_survives_egraph_clone: a clone, here a child, and the original each step their own
        /// back-off.
        /// </summary>
        [Fact]
        public void ACloneAndItsOriginalStepTheirOwn()
        {
            var (graph, copy, s) = CopyGraph();
            var bo = graph.AddScheduler(new BackOffScheduler(matchLimit: 2, banLength: 2));
            var clone = graph.Root.CreateChild();

            clone.Run(Schedule.RunWith(bo, copy));
            graph.Root.Run(Schedule.RunWith(bo, copy));

            Assert.Equal(1, clone.CountRows(s));
            Assert.Equal(1, graph.Root.CountRows(s));
        }

        /// <summary>
        /// test_top_level_let_scheduler_survives_pop_when_declared_before_push: a scheduler added before a push, here a
        /// child, still steps after the pop.
        /// </summary>
        [Fact]
        public void ASchedulerSurvivesAPop()
        {
            var (graph, copy, s) = CopyGraph();
            var bo = graph.AddScheduler(new BackOffScheduler(matchLimit: 2, banLength: 2));
            var pushed = graph.Root.CreateChild();
            pushed.Run(Schedule.RunWith(bo, copy));
            Assert.Equal(1, pushed.CountRows(s));

            pushed.Dispose();
            Assert.Equal(0, graph.Root.CountRows(s));

            graph.Root.Run(Schedule.RunWith(bo, copy));
            Assert.Equal(1, graph.Root.CountRows(s));
        }

        /// <summary>
        /// After tests/repro-scheduler-scopes.egg, whose second program saturates back-off steps of a rule that counts to
        /// 10 with a scheduler made in each pass (its <c>let-scheduler</c> is scoped to the pass); here one scheduler lives
        /// across the passes, which a host's schedule cannot make anew, to the same count.
        /// </summary>
        [Fact]
        public void ASaturationOfBackOffStepsCounts()
        {
            var graph = new EGraph();
            var rules = graph.DefineRuleset("rules");
            var r = graph.DefineRelation<long>("R");
            graph.Root.Assume(r.Of(1L));
            rules.DefineRule("count", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Where(Primitives.Less.Of(x, 10L));
                rule.Then(r.Of(Primitives.Add.Of(x, 1L)));
            });
            var bo = graph.AddScheduler(new BackOffScheduler());

            graph.Root.Run(Schedule.Saturate(Schedule.RunWith(bo, rules)));

            Assert.Equal(10, graph.Root.CountRows(r));
        }

        /// <summary>
        /// A rule with more matches than its limit is banned while the others run; when nothing else changes, the ban is
        /// fast-forwarded and its matches fire against the doubled limit.
        /// </summary>
        [Fact]
        public void ARuleOverItsLimitIsBannedWhileTheOthersRun()
        {
            var graph = new EGraph();
            var rules = graph.DefineRuleset("rules");
            var r = graph.DefineRelation<long>("R");
            var many = graph.DefineRelation<long>("Many");
            var few = graph.DefineRelation<long>("Few");
            for (var i = 0L; i < 3; i++)
                graph.Root.Assume(r.Of(i));

            var manyRule = rules.DefineRule("many", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Then(many.Of(x));
            });
            var fewRule = rules.DefineRule("few", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Where(Primitives.Less.Of(x, 1L));
                rule.Then(few.Of(x));
            });
            var bo = graph.AddScheduler(new BackOffScheduler(matchLimit: 2, banLength: 3));

            var first = graph.Root.Run(Schedule.RunWith(bo, rules));
            Assert.Equal(0, first.Rules[manyRule].Matches);
            Assert.Equal(1, first.Rules[fewRule].Matches);
            Assert.Equal(0, graph.Root.CountRows(many));

            var report = graph.Root.Run(Schedule.Repeat(10, Schedule.RunWith(bo, rules)));

            Assert.Equal(3, graph.Root.CountRows(many));
            Assert.Equal(1, graph.Root.CountRows(few));
            Assert.Equal(3, report.Rules[manyRule].Matches);
        }

        /// <summary>
        /// A negative limit or ban length is refused, as egglog-experimental's parser refuses one.
        /// </summary>
        [Fact]
        public void ANegativeSettingIsRefused()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new BackOffScheduler(matchLimit: -1));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new BackOffScheduler(banLength: -1));
        }

        /// <summary>
        /// egglog-experimental's <c>new_copy_egraph</c>: <c>R</c> holding 0 and a ruleset <c>copy</c> copying it into
        /// <c>S</c>.
        /// </summary>
        static (EGraph Graph, Ruleset Copy, Relation<long> S) CopyGraph()
        {
            var graph = new EGraph();
            var copy = graph.DefineRuleset("copy");
            var r = graph.DefineRelation<long>("R");
            var s = graph.DefineRelation<long>("S");
            graph.Root.Assume(r.Of(0L));
            copy.DefineRule("copy", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(r.Of(x));
                rule.Then(s.Of(x));
            });
            return (graph, copy, s);
        }

    }

}
