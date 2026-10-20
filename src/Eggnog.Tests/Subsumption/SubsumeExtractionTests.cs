using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Subsumption
{

    /// <summary>
    /// Extraction skips subsumed rows, ported from egglog's tests (<c>tests/integration_test.rs</c> and the unit tests
    /// of <c>src/lib.rs</c> at 5e655cae): a subsumed term is not extracted even when it is the cheapest, also after
    /// unions rewrite its arguments or its class, and a class whose only member is subsumed has no term.
    /// </summary>
    public sealed class SubsumeExtractionTests
    {

        [Fact]
        public void ASubsumedTermIsNotExtractedEvenWhenCheapest()
        {
            // test_subsumed_unextractable_action_extract.
            var t = new MathTheory();
            var expensive = t.Graph.DefineConstructor<Node>("expensive", 100, MathTheory.Subsumable);
            var cheap = t.Graph.DefineConstructor<Node>("cheap", 1, MathTheory.Subsumable);
            var root = t.Root;
            var term = root.Make(expensive.Of());
            root.Union(term, root.Make(cheap.Of()));
            Assert.Equal("(cheap)", root.Extract(term).ToString());

            root.Subsume(cheap.Of());

            Assert.Equal("(expensive)", root.Extract(term).ToString());
        }

        [Fact]
        public void ASubsumedTermMergedWithAnotherStaysUnextractable()
        {
            // test_subsume_unextractable_insert_and_merge: (f (Num 1)) is subsumed, then merged with (f (Num 2)).
            var t = new MathTheory();
            var f = t.Graph.DefineConstructor<Node, Node>("f", 1, MathTheory.Subsumable);
            var expensive = t.Graph.DefineConstructor<Node>("expensive", 100, MathTheory.Subsumable);
            var root = t.Root;
            root.Make(f.Of(t.Num.Of(1L)));
            root.Subsume(f.Of(t.Num.Of(1L)));
            root.Make(f.Of(t.Num.Of(2L)));

            root.Union(root.Get(t.Num.Of(2L)), root.Get(t.Num.Of(1L)));
            root.Union(root.Get(f.Of(t.Num.Of(2L))), root.Make(expensive.Of()));

            Assert.Equal("(expensive)", root.Extract(root.Get(f.Of(t.Num.Of(2L)))).ToString());
        }

        [Fact]
        public void TheLeftSideOfASubsumingRewriteIsNotExtracted()
        {
            // test_rewrite_subsumed_unextractable.
            var t = new MathTheory();
            var expensive = t.Graph.DefineConstructor<Node>("expensive", 100, MathTheory.Subsumable);
            var cheap = t.Graph.DefineConstructor<Node>("cheap", 1, MathTheory.Subsumable);
            t.Rules.DefineRule("cheap to expensive", r => r.Rewrite(cheap.Of(), expensive.Of(), subsume: true));
            var term = t.Root.Make(cheap.Of());

            t.Root.Run(t.Rules);

            Assert.Equal("(expensive)", t.Root.Extract(term).ToString());
        }

        [Fact]
        public void AClassWhoseOnlyTermIsSubsumedHasNoTerm()
        {
            // test_simple_extract4: (Barbar (Foobar)) cannot be extracted once (Foobar) is subsumed.
            var graph = new EGraph();
            graph.DefineSort<Node>("Foo");
            var foobar = graph.DefineConstructor<Node>("Foobar", 1, MathTheory.Subsumable);
            var barbar = graph.DefineConstructor<Node, Node>("Barbar", 1, MathTheory.Subsumable);
            var root = graph.Root;
            root.Subsume(foobar.Of());

            var x = root.Make(barbar.Of(foobar.Of()));

            Assert.Throws<InvalidOperationException>(() => root.Extract(x));
            Assert.False(root.CreateExtractor().TryExtract(x, out _));
        }

        [Fact]
        public void ATermStaysUnextractableAfterAUnionRewritesItsArgument()
        {
            // test_subsumed_unextractable_rebuild_arg.
            var t = new MathTheory();
            var container = t.Graph.DefineConstructor<Node, Node>("container", 1, MathTheory.Subsumable);
            var expensive = t.Graph.DefineConstructor<Node>("expensive", 100, MathTheory.Subsumable);
            var cheap = t.Graph.DefineConstructor<Node>("cheap", 1, MathTheory.Subsumable);
            var cheap1 = t.Graph.DefineConstructor<Node>("cheap-1", 1, MathTheory.Subsumable);
            var root = t.Root;

            // (cheap-1) is made first, so its id is the smaller one, and the union below rewrites the container's key.
            var second = root.Make(cheap1.Of());
            var res = root.Make(container.Of(cheap.Of()));
            root.Union(res, root.Make(expensive.Of()));
            var first = root.Get(cheap.Of());
            root.Subsume(container.Of(cheap.Of()));
            Assert.False(root.AreEqual(first, second));
            var before = Assert.Single(root.Rows(container)).Keys[0];

            root.Union(second, first);

            Assert.True(root.AreEqual(first, second));
            var row = Assert.Single(root.Rows(container));
            Assert.NotEqual(before, row.Keys[0]);
            Assert.True(row.Subsumed);
            Assert.Equal("(expensive)", root.Extract(res).ToString());
        }

        [Fact]
        public void AnExtractorIsStaleOnceARowItReadIsSubsumed()
        {
            var t = new MathTheory();
            var expensive = t.Graph.DefineConstructor<Node>("expensive", 100, MathTheory.Subsumable);
            var cheap = t.Graph.DefineConstructor<Node>("cheap", 1, MathTheory.Subsumable);
            var root = t.Root;
            var term = root.Make(expensive.Of());
            root.Union(term, root.Make(cheap.Of()));
            var extractor = root.CreateExtractor();
            Assert.Equal("(cheap)", extractor.Extract(term).ToString());

            root.Subsume(cheap.Of());

            Assert.Throws<InvalidOperationException>(() => extractor.Extract(term));
            Assert.Equal("(expensive)", root.CreateExtractor().Extract(term).ToString());
        }

        [Fact]
        public void ATermStaysUnextractableAfterAUnionRewritesItsClass()
        {
            // test_subsumed_unextractable_rebuild_self.
            var t = new MathTheory();
            var expensive = t.Graph.DefineConstructor<Node>("expensive", 100, MathTheory.Subsumable);
            var cheap = t.Graph.DefineConstructor<Node>("cheap", 1, MathTheory.Subsumable);
            var root = t.Root;
            var big = root.Make(expensive.Of());
            var x = root.Make(cheap.Of());
            root.Subsume(cheap.Of());

            root.Union(big, x);

            Assert.Equal("(expensive)", root.Extract(x).ToString());
        }

    }

}
