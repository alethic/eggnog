using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scheduling
{

    /// <summary>
    /// The rules of an iteration below a scope with local rules: the ruleset's global rules, then its local rules from
    /// the root down, and a rule defined since, in the ruleset or on the path, joins the next iteration. A run's
    /// statistics sum every report of a variant and list every rule that ran.
    /// </summary>
    public sealed class IterationRulesTests
    {

        /// <summary>
        /// A frozen root and a container with the local rule <c>fact(x) → seen(x)</c>.
        /// </summary>
        static (ScopeTheory T, Scope Container, Rule Local) Container()
        {
            var t = new ScopeTheory();
            t.Root.Freeze();
            var container = t.Root.CreateChild(t.Outer);
            var local = container.DefineRule(t.Rules, "fact is seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            return (t, container, local);
        }

        [Fact]
        public void TheLocalRulesComeFromTheRootDown()
        {
            var (t, outer, upper) = Container();
            using var anchor = outer.CreateChild();
            var lower = anchor.DefineRule(t.Rules, "seen is best", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(2L));
            });
            using var query = anchor.CreateChild();

            Assert.Equal([upper, lower], ScheduleExecutor.RulesFor([t.Rules], query).Select(r => r.Rule));
        }

        /// <summary>
        /// The step rule's two semi-naive variants share a description, and both run when <c>path</c> and <c>edge</c>
        /// have new rows: its matches are the sum of their reports, not the last one's.
        /// </summary>
        [Fact]
        public void AVariantsMatchesAreSummedOverItsReports()
        {
            var t = new ScopeTheory();
            var step = t.DefinePaths();
            t.Root.Assume(t.Edge.Of(7L, 8L));
            t.Root.Run(Schedule.Saturate(t.Rules));
            t.Root.Assume(t.Path.Of(1L, 2L));
            t.Root.Assume(t.Edge.Of(2L, 3L));

            var report = t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.Check(t.Path.Of(1L, 3L)));
            Assert.Equal(1, report.Rules[step].Matches);
        }

        [Fact]
        public void AnIdleQueryReportsEveryRuleThatRan()
        {
            var (t, container, local) = Container();
            var global = t.Rules.DefineRule("best of seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(1L));
            });
            container.Freeze();

            for (var i = 0; i < 2; i++)
            {
                using var query = container.CreateChild();
                query.Assume(t.Edge.Of(1L, 2L));
                var report = query.Run(Schedule.Saturate(t.Rules));

                Assert.Equal(1, report.Iterations);
                Assert.Equal(2, report.Rules.Count);
                Assert.Equal(0, report.Rules[local].Matches);
                Assert.Equal(0, report.Rules[global].Matches);
                Assert.Equal(0, report.Rules[local].IterationsWithMatches);
            }
        }

        [Fact]
        public void AGlobalRuleDefinedAfterTheFreezeJoinsTheNextQuery()
        {
            var (t, container, _) = Container();
            container.Freeze();
            using (var first = container.CreateChild())
            {
                first.Assume(t.Fact.Of("a"));
                first.Run(Schedule.Saturate(t.Rules));
                Assert.True(first.Check(t.Seen.Of("a")));
                Assert.False(first.Check(t.Best.Of("a").Is(1L)));
            }

            t.Rules.DefineRule("best of seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(1L));
            });

            using var second = container.CreateChild();
            second.Assume(t.Fact.Of("a"));
            second.Run(Schedule.Saturate(t.Rules));
            Assert.True(second.Check(t.Best.Of("a").Is(1L)));
        }

        [Fact]
        public void ALocalRuleDefinedOnTheAnchorJoinsTheNextIteration()
        {
            var (t, container, _) = Container();
            using var child = container.CreateChild();
            child.Assume(t.Fact.Of("a"));
            child.Run(Schedule.Saturate(t.Rules));

            container.DefineRule(t.Rules, "seen is best", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(2L));
            });
            child.Run(Schedule.Saturate(t.Rules));

            Assert.True(child.Check(t.Best.Of("a").Is(2L)));
        }

        /// <summary>
        /// The rules of a query below the nearest scope with local rules (the anchor) also hold the local rules of the
        /// scopes above it: one defined there after a query below the anchor ran joins the next query. <c>best</c> is a
        /// max lattice, so the outer rule's value wins only if it ran.
        /// </summary>
        [Fact]
        public void ALocalRuleDefinedAboveTheAnchorJoinsTheNextQuery()
        {
            var (t, outer, _) = Container();
            using var anchor = outer.CreateChild();
            anchor.DefineRule(t.Rules, "seen is best", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(2L));
            });

            using (var first = anchor.CreateChild())
            {
                first.Assume(t.Fact.Of("a"));
                first.Run(Schedule.Saturate(t.Rules));
                Assert.True(first.Check(t.Best.Of("a").Is(2L)));
            }

            outer.DefineRule(t.Rules, "seen is better", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Best.Of(x).Is(5L));
            });

            using var second = anchor.CreateChild();
            second.Assume(t.Fact.Of("b"));
            second.Run(Schedule.Saturate(t.Rules));
            Assert.True(second.Check(t.Best.Of("b").Is(5L)));
            Assert.Equal(3, ScheduleExecutor.RulesFor([t.Rules], second).Count);
        }

        [Fact]
        public void ARuleOfAnotherRulesetIsNotInTheList()
        {
            var (t, container, local) = Container();
            var other = t.Graph.DefineRuleset("other");
            container.DefineRule(other, "other local", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Best.Of(x).Is(3L));
            });
            container.Freeze();
            using var query = container.CreateChild();

            Assert.Equal([local], ScheduleExecutor.RulesFor([t.Rules], query).Select(r => r.Rule));
            Assert.Equal(2, ScheduleExecutor.RulesFor([t.Rules, other], query).Count);
        }

    }

}
