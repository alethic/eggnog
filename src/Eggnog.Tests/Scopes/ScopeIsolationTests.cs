using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Isolation (context = copy): what a query scope derives stays in it, and a child sees what its parent sees.
    /// </summary>
    public sealed class ScopeIsolationTests
    {

        [Fact]
        public void AQuerysDerivedFactsAreVisibleNeitherInItsSchemaNorInItsSiblings()
        {
            var t = new ScopeTheory();
            t.Rules.DefineRule("seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x), t.Best.Of(x).Is(1L));
            });
            t.Root.Freeze();
            var schema = t.Root.CreateChild(t.Outer);
            schema.Assume(t.Fact.Of("schema"));
            schema.Run(Schedule.Saturate(t.Rules));
            schema.Freeze();

            using var one = schema.CreateChild(t.Inner);
            using var two = schema.CreateChild(t.Inner);
            one.Assume(t.Fact.Of("one"), t.Best.Of("schema").Is(5L));
            one.Run(Schedule.Saturate(t.Rules));
            two.Run(Schedule.Saturate(t.Rules));

            Assert.True(one.Check(t.Seen.Of("one"), t.Seen.Of("schema")));
            Assert.Equal(5L, one.Get(t.Best.Of("schema")));
            Assert.False(schema.Check(t.Seen.Of("one")));
            Assert.Equal(1L, schema.Get(t.Best.Of("schema")));
            Assert.False(two.Check(t.Fact.Of("one")));
            Assert.False(two.Check(t.Seen.Of("one")));
            Assert.Equal(1L, two.Get(t.Best.Of("schema")));
            Assert.Equal(2, schema.CountRows(t.Fact) + schema.CountRows(t.Seen));
        }

        [Fact]
        public void ConstantLosesRootUnionBeforeChildRuns()
        {
            var t = new NodeTheory();
            var link = t.Graph.DefineRelation<Node, long>("link");
            var hit = t.Graph.DefineRelation<long>("hit");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Rules.DefineRule("watch b", r =>
            {
                var n = r.Var<long>("n");
                r.When(link.Of(b, n));
                r.Then(hit.Of(n));
            });
            t.Root.Assume(link.Of(a, 3L), link.Of(b, 1L));

            // b loses a union at the root before the child exists; the child asserts nothing and sees what the root
            // sees.
            Assert.True(t.Root.Union(a, b));
            var child = t.Root.CreateChild();
            child.Run(Schedule.Saturate(t.Rules));

            Assert.True(child.Check(hit.Of(1L)));
            Assert.True(child.Check(hit.Of(3L)));
            Assert.False(t.Root.Check(hit.Of(3L)));
            Assert.True(child.AreEqual(a, b));
            Assert.Equal(child.Find(a), child.Find(b));
        }

        [Fact]
        public void AChildSeesWritesItsWritableParentMakesAfterItWasCreated()
        {
            var t = new ScopeTheory();
            t.DefinePaths();
            t.Root.Assume(t.Edge.Of(1L, 2L));
            var child = t.Root.CreateChild();
            child.Assume(t.Edge.Of(3L, 4L));
            child.Run(Schedule.Saturate(t.Rules));
            Assert.False(child.Check(t.Path.Of(1L, 4L)));

            t.Root.Assume(t.Edge.Of(2L, 3L));
            child.Run(Schedule.Saturate(t.Rules));

            Assert.True(child.Check(t.Path.Of(1L, 4L)));
            Assert.False(t.Root.Check(t.Edge.Of(3L, 4L)));
            Assert.False(t.Root.Check(t.Path.Of(1L, 2L)));
        }

    }

}
