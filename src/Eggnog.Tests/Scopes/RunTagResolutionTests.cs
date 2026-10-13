using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// <c>Run</c> resolves every tag a rule the schedule reaches reads before any work: a tag that does not resolve
    /// from the scope throws before the first step, so the steps before the offending one write nothing. Queries
    /// resolve their tags before any work too, and an <c>Until</c> fact whose tag does not resolve is refused before
    /// any work.
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

        /// <summary>
        /// An unresolved tag at a query is an <see cref="InvalidOperationException"/> before any work: a query reading
        /// two tags, one of which does not resolve, does not sync the other's mirror first.
        /// </summary>
        [Fact]
        public void AnUnresolvedTagAtAQueryIsRefusedBeforeAnyWork()
        {
            var t = new ScopeTheory();
            var query = t.Graph.DefineQuery("two tags", q =>
            {
                var x = q.Var<string>("x");
                q.When(t.Fact.In(t.Outer).Of(x), t.Seen.In(t.Inner).Of(x));
            });
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"));
            var version = outer.Version;

            Assert.Throws<InvalidOperationException>(() => outer.Count(query));

            Assert.Equal(version, outer.Version);
        }

        /// <summary>
        /// An <c>Until</c> fact whose tag does not resolve is refused before any work, as an
        /// <see cref="ArgumentException"/> (an <c>Until</c> fact the scope cannot read), not as the
        /// <see cref="InvalidOperationException"/> of an unresolved tag at <c>Run</c>.
        /// </summary>
        [Fact]
        public void AnUntilFactWithAnUnresolvedTagIsAnArgumentException()
        {
            var t = new ScopeTheory();
            var scope = t.Root.CreateChild(t.Outer);

            Assert.Throws<ArgumentException>(() => scope.Run(Schedule.Until(t.Rules, t.Fact.In(t.Inner).Of("a"))));
        }

        /// <summary>
        /// An unresolved tag at a query is an <see cref="InvalidOperationException"/> before any work: a query reading
        /// two tags, the first resolving to the writable scope and stale, the second unresolved, leaves the first
        /// scope's Version where it was.
        /// </summary>
        [Fact]
        public void AQueryWithAnUnresolvedTagIsRefusedBeforeAnyWork()
        {
            var t = new ScopeTheory();
            var both = t.Graph.DefineQuery("both", q =>
            {
                var x = q.Var<string>("x");
                q.When(t.Fact.In(t.Outer).Of(x), t.Fact.In(t.Inner).Of(x));
            });
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"));
            var version = outer.Version;

            Assert.Throws<InvalidOperationException>(() => outer.Count(both));

            Assert.Equal(version, outer.Version);
        }

    }

}
