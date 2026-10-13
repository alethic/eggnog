using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// The depth limit of a scope tree (colors X36, §6.7): the scope layer refuses a child below depth 31 itself, with its
    /// own message, before the engine is asked, and leaves the tree as it was.
    /// </summary>
    public sealed class ScopeDepthTests
    {

        [Fact]
        public void TheScopeLayerRefusesAChildBelowDepth31WithItsOwnMessage()
        {
            var t = new ScopeTheory();
            var scope = t.Root;
            for (var depth = 1; depth <= 31; depth++)
                scope = scope.CreateChild();

            var e = Assert.Throws<InvalidOperationException>(() => scope.CreateChild(t.Outer));

            Assert.Equal($"Scope '{scope.Path}' is at depth 31, the deepest a scope tree goes (colors X36).", e.Message);
            Assert.Empty(scope.Children);
            Assert.Equal(0, scope.Engine.Context.Context.ChildCount);
            Assert.False(scope.IsFaulted);
            scope.Assume(t.Fact.Of("deepest"));
            Assert.True(scope.Check(t.Fact.Of("deepest")));
        }

    }

}
