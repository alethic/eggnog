using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// Class constants that lose a union (D3, colors X19/I30), by behaviour only: these tests run unchanged in R2, where
    /// the engine canonicalizes constants itself.
    /// </summary>
    public sealed class IdConstantTests
    {

        [Fact]
        public void ARuleWithAClassConstantMatchesTheClassAfterTheConstantLosesAUnion()
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
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(hit.Of(1L)));
            Assert.False(t.Root.Check(hit.Of(3L)));

            Assert.True(t.Root.Union(a, b));
            Assert.Equal(a.Id, t.Root.Find(b).Id);
            t.Root.Assume(link.Of(a, 2L));
            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.Check(hit.Of(2L)));
            Assert.True(t.Root.Check(hit.Of(3L)));
        }

        [Fact]
        public void AnActionWritingSuchAConstantWritesTheCanonicalCell()
        {
            var t = new NodeTheory();
            var trigger = t.Graph.DefineRelation<long>("trigger");
            var mark = t.Graph.DefineRelation<Node>("mark");
            var both = t.Graph.DefineRelation<Node>("both");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Rules.DefineRule("mark b", r =>
            {
                var n = r.Var<long>("n");
                r.When(trigger.Of(n));
                r.Then(mark.Of(b));
            });
            t.Rules.DefineRule("both", r =>
            {
                var x = r.Var<Node>("x");
                r.When(mark.Of(x), t.Tag.Of(x));
                r.Then(both.Of(x));
            });
            t.Root.Assume(t.Tag.Of(a));
            t.Root.Union(a, b);
            Assert.Equal(a.Id, t.Root.Find(b).Id);
            t.Root.Assume(trigger.Of(5L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.Check(mark.Of(a)));
            Assert.True(t.Root.Check(both.Of(a)));
        }

    }

}
