using Eggnog.Tests.Support;
using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review probes of §6.10: classes in extracted terms are canonical in the extractor's scope.
    /// </summary>
    public sealed class ExtractionReviewTests
    {

        [Fact]
        public void ChildClassesAreCanonical()
        {
            var t = new NodeTheory();
            var root = t.Root;
            var b = root.Make(t.B.Of());
            var wrapped = root.Make(t.Wrap.Of(t.A.Of()));
            var a = root.Make(t.A.Of());
            root.Union(b, a);
            var canonical = root.Find(a);
            var term = root.Extract(wrapped);
            Assert.Equal(canonical.Id, term.Child(0).Class);
            Assert.True(term.TryMatch(t.Wrap, out var child));
            Assert.Equal(canonical, child.Value);
            Assert.Equal(canonical, term.Value<Node>(0));
            Assert.Equal(root.Find(wrapped).Id, term.Class);
        }

        [Fact]
        public void IntAndLongValueCostsAreSeparate()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var both = graph.DefineConstructor<int, long, Node>("Both");
            var n = graph.Root.Make(both.Of(1, 2L));
            var costs = CostModel.Default.WithValueCost(graph.Values.TypeOf<int>(), 10);
            Assert.Equal(12, graph.Root.Extract(n, costs).Cost);
            Assert.Equal(3, graph.Root.Extract(n).Cost);
        }

    }

}
