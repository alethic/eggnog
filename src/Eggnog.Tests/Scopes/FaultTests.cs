using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Faults in children (D18, X12): a child faulted by a <see cref="RuleEvaluationException"/> allows only
    /// <see cref="Scope.Dispose"/>, keeps its cause, and leaves its parent and siblings unaffected.
    /// </summary>
    public sealed class FaultTests
    {

        [Fact]
        public void AChildIsFaultedAfterAnEvaluationFailureAndOnlyDisposeWorks()
        {
            var t = new ScopeTheory();
            t.Rules.DefineRule("fails on bad", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Where(x, x => x == "bad" ? throw new InvalidOperationException("bad fact") : true);
                r.Then(t.Seen.Of(x));
            });
            t.Root.Assume(t.Fact.Of("good"));
            t.Root.Freeze();
            var bad = t.Root.CreateChild(t.Outer);
            var sibling = t.Root.CreateChild(t.Outer);
            bad.Assume(t.Fact.Of("bad"));

            var failure = Assert.Throws<RuleEvaluationException>(() => bad.Run(Schedule.Saturate(t.Rules)));

            Assert.Equal("fails on bad", failure.RuleName);
            Assert.True(bad.IsFaulted);
            Assert.Same(failure, bad.Failure);
            var refused = Assert.Throws<InvalidOperationException>(() => bad.Check(t.Fact.Of("good")));
            Assert.Same(failure, refused.InnerException);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => bad.Assume(t.Fact.Of("x"))).InnerException);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => bad.Run(t.Rules)).InnerException);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => bad.CreateChild()).InnerException);

            // The parent and the sibling are unaffected.
            Assert.False(t.Root.IsFaulted);
            Assert.False(sibling.IsFaulted);
            sibling.Run(Schedule.Saturate(t.Rules));
            Assert.True(sibling.Check(t.Seen.Of("good")));
            Assert.True(t.Root.Check(t.Fact.Of("good")));

            bad.Dispose();
            Assert.True(bad.IsDisposed);
            Assert.True(bad.Engine.IsDisposed);
        }

        [Fact]
        public void AWritableParentStaysUsableAfterItsChildFaults()
        {
            var t = new ScopeTheory();
            var function = t.Graph.DefineFunction<string, long>("plain");
            t.Rules.DefineRule("conflict", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(function.Of(x).Is(1L), function.Of(x).Is(2L));
            });
            var child = t.Root.CreateChild();
            child.Assume(t.Fact.Of("a"));

            Assert.Throws<RuleEvaluationException>(() => child.Run(t.Rules));
            Assert.True(child.IsFaulted);
            Assert.NotNull(child.Failure);
            Assert.False(t.Root.IsFaulted);
            t.Root.Assume(t.Seen.Of("root"));
            Assert.True(t.Root.Check(t.Seen.Of("root")));
            child.Dispose();
            Assert.Empty(t.Root.Children);
        }

    }

}
