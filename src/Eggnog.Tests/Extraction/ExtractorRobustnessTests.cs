using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Extraction
{

    /// <summary>
    /// The extractor on terms of any depth, which it wraps without recursing once per level, and its staleness when a
    /// constructor defined after it gets rows, which it would otherwise miss.
    /// </summary>
    public sealed class ExtractorRobustnessTests
    {

        const int Depth = 100_000;

        /// <summary>
        /// A chain of 100,000 <c>Wrap</c>s over <c>A</c> extracts, as its cheapest term, its variants and its cost, on a
        /// test thread's ordinary stack: the library wraps the engine's term on a stack of its own.
        /// </summary>
        [Fact]
        public void ATermOfAnyDepthExtracts()
        {
            var t = new NodeTheory();
            var top = default(Node);
            t.Root.Write(w =>
            {
                top = w.Make(t.A.Of());
                for (var i = 0; i < Depth; i++)
                    top = w.Make(t.Wrap.Of(top));
            });

            var term = t.Root.Extract(top);
            var variants = t.Root.ExtractVariants(top, 2);

            var depth = 0;
            for (var node = term; node.Is(t.Wrap); node = node.Child(0))
                depth++;

            Assert.Equal(Depth, depth);
            Assert.Equal(Depth + 1, term.Cost);
            Assert.True(term.Mentions(t.A));
            Assert.Equal(term.Cost, Assert.Single(variants).Cost);
            Assert.StartsWith("(Wrap (Wrap ", term.ToString());
        }

        /// <summary>
        /// A constructor defined after the extractor leaves it usable while it has no rows; once it has one, the extractor,
        /// which reads only the constructors it saw, throws as stale instead of answering the dearer term, and a new one
        /// answers the cheaper.
        /// </summary>
        [Fact]
        public void AConstructorDefinedLaterMakesTheExtractorStaleOnceItHasRows()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var dear = graph.DefineConstructor<Node>("Dear", cost: 10);
            var x = graph.Root.Make(dear.Of());
            var extractor = graph.Root.CreateExtractor();
            var cheap = graph.DefineConstructor<Node>("Cheap", cost: 1);

            Assert.Equal("(Dear)", extractor.Extract(x).ToString());

            graph.Root.Assume(cheap.Of().Is(x));

            var e = Assert.Throws<InvalidOperationException>(() => extractor.Extract(x));
            Assert.Contains("'Cheap' was defined after the extractor was created", e.Message);
            Assert.Throws<InvalidOperationException>(() => extractor.TryGetCost(x, out _));
            Assert.Throws<InvalidOperationException>(() => extractor.ExtractVariants(x, 2));
            Assert.Equal("(Cheap)", graph.Root.Extract(x).ToString());
        }

        /// <summary>
        /// The same in a child: a constructor defined once the root is frozen, with a row in the child, makes the child's
        /// extractor stale.
        /// </summary>
        [Fact]
        public void AConstructorDefinedLaterMakesAChildsExtractorStale()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var dear = graph.DefineConstructor<Node>("Dear", cost: 10);
            var x = graph.Root.Make(dear.Of());
            graph.Root.Freeze();
            var child = graph.Root.CreateChild();
            var extractor = child.CreateExtractor();
            var cheap = graph.DefineConstructor<Node>("Cheap", cost: 1);

            Assert.Equal("(Dear)", extractor.Extract(x).ToString());

            child.Assume(cheap.Of().Is(x));

            Assert.Throws<InvalidOperationException>(() => extractor.Extract(x));
            Assert.Equal("(Cheap)", child.Extract(x).ToString());
            Assert.Equal("(Dear)", graph.Root.CreateExtractor().Extract(x).ToString());
        }

        /// <summary>
        /// A later constructor that extraction may not use leaves the extractor usable, rows and all.
        /// </summary>
        [Fact]
        public void ALaterUnextractableConstructorLeavesTheExtractorUsable()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var dear = graph.DefineConstructor<Node>("Dear", cost: 10);
            var x = graph.Root.Make(dear.Of());
            var extractor = graph.Root.CreateExtractor();
            var hidden = graph.DefineConstructor<Node>("Hidden", cost: 1, options: new TableOptions { Extractable = false });

            graph.Root.Assume(hidden.Of().Is(x));

            Assert.Equal("(Dear)", extractor.Extract(x).ToString());
        }

    }

}
