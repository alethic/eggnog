using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Faults in children: a child that throws is faulted, since the failure may have left a hide whose copy was never
    /// merged, which cannot be repaired. A child faulted by a <see cref="RuleEvaluationException"/> allows only
    /// <see cref="Scope.Dispose"/>, keeps its cause, and leaves its parent and siblings unaffected.
    /// </summary>
    public sealed class FaultTests
    {

        [Fact]
        public void AChildStaysUsableAfterAFailedRun()
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

            // As egglog's (fail (run 1)) in a push: the run fails, keeps what its other matches wrote, and the scope goes on.
            Assert.Equal("fails on bad", failure.RuleName);
            Assert.False(bad.IsFaulted);
            Assert.Null(bad.Failure);
            Assert.True(bad.Check(t.Fact.Of("good")));
            Assert.True(bad.Check(t.Seen.Of("good")));
            bad.Assume(t.Fact.Of("x"));
            bad.Run(t.Rules);
            Assert.True(bad.Check(t.Seen.Of("x")));
            bad.CreateChild().Dispose();

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
        public void ACaseTheHostRunsStaysUsableAfterAFailedRun()
        {
            // A case of a CaseSet is a scope the host runs, as it runs a child: its failed run leaves it usable, and the
            // set still lifts what the case proved.
            var t = new ScopeTheory();
            t.Rules.DefineRule("fails on bad", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Where(x, x => x == "bad" ? throw new InvalidOperationException("bad fact") : true);
                r.Then(t.Seen.Of(x));
            });
            t.Root.Assume(t.Fact.Of("good"));
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            @case.Assume(t.Fact.Of("bad"));

            var failure = Assert.Throws<RuleEvaluationException>(() => @case.Run(Schedule.Saturate(t.Rules)));

            Assert.Equal("fails on bad", failure.RuleName);
            Assert.False(@case.IsFaulted);
            Assert.True(@case.Check(t.Seen.Of("good")));
            Assert.False(parent.IsFaulted);
            cases.Lift();
            Assert.True(parent.Check(t.Seen.Of("good")));
            Assert.False(parent.Check(t.Seen.Of("bad")));
        }

        [Fact]
        public void AWritableParentStaysUsableAfterItsChildsRunFails()
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
            Assert.False(child.IsFaulted);
            Assert.True(child.Check(t.Fact.Of("a")));
            Assert.False(t.Root.IsFaulted);
            t.Root.Assume(t.Seen.Of("root"));
            Assert.True(t.Root.Check(t.Seen.Of("root")));
            child.Dispose();
            Assert.Empty(t.Root.Children);
        }

    }

}
