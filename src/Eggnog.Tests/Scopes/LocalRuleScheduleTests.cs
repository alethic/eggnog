using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// How a schedule picks local rules: a local rule joins an iteration only when its own ruleset is among the
    /// iteration's rulesets and the iteration runs in its owner or a descendant of it.
    /// </summary>
    public sealed class LocalRuleScheduleTests
    {

        [Fact]
        public void ALocalRuleRunsOnlyWhenItsOwnRulesetIsScheduled()
        {
            var t = new ScopeTheory();
            var others = t.Graph.DefineRuleset("others");
            others.DefineRule("paths elsewhere", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Path.Of(x, y));
                r.Then(t.Fact.Of("path"));
            });
            t.Root.Freeze();
            var scope = t.Root.CreateChild(t.Outer);
            var local = scope.DefineRule(t.Rules, "local sees", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            scope.Assume(t.Fact.Of("a"), t.Path.Of(1L, 2L));

            // Only the other ruleset: the local rule, scheduled with t.Rules, stays out.
            var report = scope.Run(Schedule.Saturate(others));

            Assert.True(scope.Check(t.Fact.Of("path")));
            Assert.False(scope.Check(t.Seen.Of("a")));
            Assert.False(scope.Check(t.Seen.Of("path")));
            Assert.False(report.Rules.ContainsKey(local));

            // Its own ruleset runs it.
            report = scope.Run(Schedule.Saturate(t.Rules));
            Assert.True(scope.Check(t.Seen.Of("a"), t.Seen.Of("path")));
            Assert.True(report.Rules[local].Matches >= 2);
        }

        [Fact]
        public void ALocalRuleRunsInADescendantOnlyWithItsOwnRuleset()
        {
            var t = new ScopeTheory();
            var others = t.Graph.DefineRuleset("others");
            t.Root.Freeze();
            var schema = t.Root.CreateChild(t.Outer);
            var local = schema.DefineRule(t.Rules, "local sees", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            schema.Freeze();
            var query = schema.CreateChild(t.Inner);
            query.Assume(t.Fact.Of("q"));

            var report = query.Run(Schedule.Saturate(others));

            Assert.False(query.Check(t.Seen.Of("q")));
            Assert.False(report.Rules.ContainsKey(local));
            query.Run(Schedule.Saturate(t.Rules));
            Assert.True(query.Check(t.Seen.Of("q")));
        }

    }

}
