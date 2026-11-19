using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// <see cref="Scope.Children"/> lists live children only: a disposed child whose release waits for a live child of
    /// its own is not listed, although its engine state is kept.
    /// </summary>
    public sealed class DisposedChildListingTests
    {

        [Fact]
        public void ADisposedChildWaitingForItsReleaseIsNotListed()
        {
            var t = new ScopeTheory();
            var kept = t.Root.CreateChild();
            var waiting = t.Root.CreateChild();
            var grandchild = waiting.CreateChild();

            waiting.Dispose();

            Assert.False(waiting.Engine.IsDisposed);
            Assert.Equal(new[] { kept }, t.Root.Children);
            Assert.Equal(new[] { grandchild }, waiting.Children);
            Assert.Equal(2, t.Root.Engine.Context.Context.ChildCount);

            grandchild.Dispose();
            Assert.True(waiting.Engine.IsDisposed);
            Assert.Equal(new[] { kept }, t.Root.Children);
            Assert.Equal(1, t.Root.Engine.Context.Context.ChildCount);
        }

    }

}
