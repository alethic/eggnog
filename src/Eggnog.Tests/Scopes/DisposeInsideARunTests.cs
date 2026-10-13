using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A scope disposed from inside an engine call of its thread domain (D40): the engine would refuse the disposal as
    /// re-entrant, so the release waits for the domain's last exit, even for a scope that has no call of its own in
    /// progress (a sibling of the running scope, under the same writable parent).
    /// </summary>
    public sealed class DisposeInsideARunTests
    {

        [Fact]
        public void ASiblingDisposedFromAPrimitiveOfARunInItsDomainIsReleasedWhenTheRunEnds()
        {
            var t = new ScopeTheory();
            Scope? sibling = null;
            t.Rules.DefineRule("disposes the sibling", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Where(x, _ =>
                {
                    sibling!.Dispose();
                    return true;
                });
                r.Then(t.Seen.Of(x));
            });
            var running = t.Root.CreateChild(t.Outer);
            sibling = t.Root.CreateChild(t.Outer);
            Assert.Same(running.Guard, sibling.Guard);
            running.Assume(t.Fact.Of("a"));

            var report = running.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(running.Check(t.Seen.Of("a")));
            Assert.True(sibling.IsDisposed);
            Assert.True(sibling.Engine.IsDisposed);
            Assert.Equal(new[] { running }, t.Root.Children);
            Assert.Equal(1, t.Root.Engine.Context.Context.ChildCount);
        }

    }

}
