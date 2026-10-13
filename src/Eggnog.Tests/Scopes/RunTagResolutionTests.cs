using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// <c>Run</c> resolves every tag a rule the schedule reaches reads before any work (§6.5.3, §6.7): a tag that does not
    /// resolve from the scope throws before the first step, so the steps before the offending one write nothing.
    /// </summary>
    public sealed class RunTagResolutionTests
    {

        [Fact]
        public void ARunWhoseLaterStepReadsATagThatDoesNotResolveWritesNothing()
        {
            var t = new ScopeTheory();
            var first = t.Graph.DefineRuleset("first");
            first.DefineRule("see", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            var later = t.Graph.DefineRuleset("later");
            later.DefineRule("inner reader", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.In(t.Inner).Of(x));
                r.Then(t.Fact.Of("never"));
            });
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"));
            var version = outer.Version;

            var e = Assert.Throws<InvalidOperationException>(() => outer.Run(Schedule.Sequence(Schedule.Saturate(first), Schedule.Saturate(later))));

            Assert.Equal($"Rule 'inner reader' reads scope 'inner', but '{outer.Path}' has no ancestor tagged 'inner'.", e.Message);
            Assert.False(outer.Check(t.Seen.Of("a")));
            Assert.Equal(version, outer.Version);
            Assert.False(outer.IsFaulted);
        }

        [Fact]
        public void ARunReachingALocalRuleThatReadsATagThatDoesNotResolveWritesNothing()
        {
            var t = new ScopeTheory();
            t.Rules.DefineRule("see", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            var later = t.Graph.DefineRuleset("later");
            var outer = t.Root.CreateChild(t.Outer);
            outer.DefineRule(later, "inner reader", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.In(t.Inner).Of(x));
                r.Then(t.Fact.Of("never"));
            });
            outer.Assume(t.Fact.Of("a"));

            Assert.Throws<InvalidOperationException>(() => outer.Run(Schedule.Sequence(Schedule.Repeat(1, t.Rules), Schedule.Repeat(1, later))));

            Assert.False(outer.Check(t.Seen.Of("a")));

            // The same schedule runs in a descendant tagged inner.
            var inner = outer.CreateChild(t.Inner);
            inner.Run(Schedule.Sequence(Schedule.Repeat(1, t.Rules), Schedule.Repeat(1, later)));
            Assert.True(inner.Check(t.Seen.Of("a"), t.Fact.Of("never")));
        }

    }

}
