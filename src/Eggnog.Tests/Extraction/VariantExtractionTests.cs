using System;
using System.Linq;

using Eggnog.Tests.Subsumption;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Extraction
{

    /// <summary>
    /// Extracting several variants of a class, egglog's <c>(extract x n)</c>: egglog's variants tests
    /// (<c>tests/integration_test.rs</c> at 5e655cae), the order under the count and the cost model, the choice leading
    /// with zero costs, the nodes that are not variants (subsumed, unextractable, or with an argument that has no term),
    /// sharing, a class that contains itself, staleness, and the scope cases: a child sees its own and its ancestors'
    /// nodes, joins two parent classes, and hides what it subsumes, and a frozen scope extracts on many threads.
    /// </summary>
    public sealed class VariantExtractionTests
    {

        [Fact]
        public void TheVariantsOfAClassComeCheapestFirst()
        {
            // test_extract_variants1.
            var graph = new EGraph();
            graph.DefineSort<Node>("Term");
            var origin = graph.DefineConstructor<Node>("Origin", 0);
            var bigStep = graph.DefineConstructor<Node, Node>("BigStep", 10);
            var smallStep = graph.DefineConstructor<Node, Node>("SmallStep", 1);
            var root = graph.Root;
            var t = root.Make(origin.Of());
            var tb = root.Make(bigStep.Of(t));
            var tbs = root.Make(smallStep.Of(tb));
            var ts = root.Make(smallStep.Of(t));
            var tss = root.Make(smallStep.Of(ts));
            var tsss = root.Make(smallStep.Of(tss));
            root.Union(tbs, tsss);
            var tssss = root.Make(smallStep.Of(tsss));
            root.Union(tssss, tb);

            var variants = root.ExtractVariants(tb, 3);

            Assert.Equal(["(SmallStep (SmallStep (SmallStep (SmallStep (Origin)))))", "(BigStep (Origin))"], variants.Select(v => v.ToString()));
            Assert.Equal([4L, 10L], variants.Select(v => v.Cost));
            Assert.All(variants, v => Assert.Equal(root.Find(tb), root.Find(new Node(v.Class))));
        }

        [Fact]
        public void ASubsumedNodeIsNotAVariant()
        {
            // test_subsume_unextractable_action_extract_multiple.
            var t = new MathTheory();
            var root = t.Root;
            var one = root.Make(t.Num.Of(1L));
            root.Union(one, root.Make(t.Num.Of(2L)));
            Assert.Equal(["(Num 1)", "(Num 2)"], root.ExtractVariants(one, 2).Select(v => v.ToString()));

            root.Subsume(t.Num.Of(2L));

            Assert.Equal("(Num 1)", Assert.Single(root.ExtractVariants(one, 2)).ToString());
        }

        [Fact]
        public void ASubsumedTermIsNeitherRewrittenNorAVariant()
        {
            // test_subsume: the commuting rule skips the subsumed (Add (Num 1) (Num 2)), so x has no variant until a
            // union gives its class another node.
            var t = new MathTheory();
            t.Rules.DefineRule("commute", r =>
            {
                var (a, b) = r.Vars<Node, Node>("a", "b");
                r.Rewrite(t.Add.Of(a, b), t.Add.Of(b, a));
            });
            var root = t.Root;
            var x = root.Make(t.Add.Of(t.Num.Of(1L), t.Num.Of(2L)));
            var y = root.Make(t.Add.Of(t.Num.Of(3L), t.Num.Of(4L)));
            root.Subsume(t.Add.Of(t.Num.Of(1L), t.Num.Of(2L)));
            root.Run(t.Rules);

            var ofY = root.ExtractVariants(y, 10);
            Assert.Equal(2, ofY.Count);
            Assert.Equal(root.Extract(y).ToString(), ofY[0].ToString());
            Assert.Equal(["(Add (Num 3) (Num 4))", "(Add (Num 4) (Num 3))"], ofY.Select(v => v.ToString()).Order());
            Assert.Empty(root.ExtractVariants(x, 10));

            var otherConst = t.Graph.DefineConstructor<Node>("otherConst");
            root.Union(x, root.Make(otherConst.Of()));

            Assert.Equal("(otherConst)", Assert.Single(root.ExtractVariants(x, 10)).ToString());
        }

        [Fact]
        public void TheCountBoundsTheVariants()
        {
            var t = new NodeTheory();
            var slow = t.Graph.DefineConstructor<long, Node>("Slow", cost: 100);
            var x = t.Root.Make(slow.Of(1L));
            t.Root.Union(x, t.Root.Make(t.Lit.Of(1L)));
            t.Root.Union(x, t.Root.Make(t.A.Of()));
            var extractor = t.Root.CreateExtractor();

            Assert.Empty(extractor.ExtractVariants(x, 0));
            Assert.Equal(["(A)"], extractor.ExtractVariants(x, 1).Select(v => v.ToString()));
            Assert.Equal(["(A)", "(Lit 1)"], extractor.ExtractVariants(x, 2).Select(v => v.ToString()));
            Assert.Equal(["(A)", "(Lit 1)", "(Slow 1)"], extractor.ExtractVariants(x, 3).Select(v => v.ToString()));
            Assert.Equal([1L, 2L, 101L], extractor.ExtractVariants(x, 100).Select(v => v.Cost));
            Assert.Equal(extractor.Extract(x).ToString(), extractor.ExtractVariants(x, 1)[0].ToString());
            Assert.Throws<ArgumentOutOfRangeException>(() => extractor.ExtractVariants(x, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => t.Root.ExtractVariants(x, -1));
        }

        [Fact]
        public void TheCostModelOrdersTheVariants()
        {
            var t = new NodeTheory();
            var slow = t.Graph.DefineConstructor<long, Node>("Slow", cost: 100);
            var x = t.Root.Make(slow.Of(1L));
            t.Root.Union(x, t.Root.Make(t.Lit.Of(1L)));

            Assert.Equal(["(Lit 1)", "(Slow 1)"], t.Root.ExtractVariants(x, 2).Select(v => v.ToString()));
            var cheapSlow = t.Root.ExtractVariants(x, 2, CostModel.Default.With(slow, 0));
            Assert.Equal(["(Slow 1)", "(Lit 1)"], cheapSlow.Select(v => v.ToString()));
            Assert.Equal([1L, 2L], cheapSlow.Select(v => v.Cost));
            Assert.Equal(["(Slow 1)"], t.Root.ExtractVariants(x, 2, CostModel.Default.Unextractable(t.Lit)).Select(v => v.ToString()));
        }

        [Fact]
        public void WithZeroCostsTheChoiceStillLeads()
        {
            // F comes first in content order, but Knuth's algorithm finalizes the class through G, whose node has no
            // child to wait for.
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var f = graph.DefineConstructor<Node, Node>("F", 0);
            var g = graph.DefineConstructor<Node>("G", 0);
            var h = graph.DefineConstructor<Node>("H", 0);
            var x = graph.Root.Make(f.Of(h.Of()));
            graph.Root.Union(x, graph.Root.Make(g.Of()));

            var variants = graph.Root.ExtractVariants(x, 5);

            Assert.Equal("(G)", graph.Root.Extract(x).ToString());
            Assert.Equal(["(G)", "(F (H))"], variants.Select(v => v.ToString()));
            Assert.Equal([0L, 0L], variants.Select(v => v.Cost));
        }

        [Fact]
        public void ANodeWhoseArgumentHasNoTermIsNotAVariant()
        {
            // egglog panics on such a node (src/extract.rs:691); here it is left out, as Extract leaves it out.
            var t = new NodeTheory();
            var hidden = t.Graph.DefineConstructor<Node>("Hidden", options: new TableOptions { Extractable = false });
            var wrapped = t.Root.Make(t.Wrap.Of(hidden.Of()));
            Assert.Empty(t.Root.ExtractVariants(wrapped, 5));
            Assert.Empty(t.Root.ExtractVariants(t.Root.Get(hidden.Of()), 5));

            t.Root.Union(wrapped, t.Root.Make(t.B.Of()));

            Assert.Equal("(B)", Assert.Single(t.Root.ExtractVariants(wrapped, 5)).ToString());
        }

        [Fact]
        public void VariantsShareTheirSubterms()
        {
            var t = new NodeTheory();
            var x = t.Root.Make(t.Wrap.Of(t.Lit.Of(1L)));
            t.Root.Union(x, t.Root.Make(t.Add.Of(t.Lit.Of(1L), t.Lit.Of(1L))));

            var variants = t.Root.ExtractVariants(x, 2);

            Assert.Equal(["(Wrap (Lit 1))", "(Add (Lit 1) (Lit 1))"], variants.Select(v => v.ToString()));
            Assert.Equal([3L, 5L], variants.Select(v => v.Cost));
            Assert.Same(variants[0].Child(0), variants[1].Child(0));
            Assert.Same(variants[1].Child(0), variants[1].Child(1));
            Assert.Equal(variants[0].Class, variants[1].Class);
            Assert.True(variants[1].TryMatch(t.Add, out var left, out _));
            Assert.Equal(2, left.Term!.Cost);
        }

        [Fact]
        public void AVariantOfAClassThatContainsItselfHasItsCheapestTermBelow()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            t.Root.Union(a, t.Root.Make(t.Wrap.Of(a)));

            var variants = t.Root.ExtractVariants(a, 5);

            Assert.Equal(["(A)", "(Wrap (A))"], variants.Select(v => v.ToString()));
            Assert.Equal([1L, 2L], variants.Select(v => v.Cost));
            Assert.Equal(1, variants[1].Child(0).Cost);
        }

        [Fact]
        public void AStaleExtractorRefusesVariants()
        {
            var t = new NodeTheory();
            var x = t.Root.Make(t.A.Of());
            var extractor = t.Root.CreateExtractor();
            Assert.Single(extractor.ExtractVariants(x, 5));

            t.Root.Union(x, t.Root.Make(t.B.Of()));

            Assert.Throws<InvalidOperationException>(() => extractor.ExtractVariants(x, 5));
            Assert.Equal(["(A)", "(B)"], t.Root.ExtractVariants(x, 5).Select(v => v.ToString()));
        }

        [Fact]
        public void AChildsVariantsIncludeItsOwnNodesAndCarryItsOrigin()
        {
            var t = new NodeTheory();
            var sum = t.Root.Make(t.Add.Of(t.Lit.Of(1L), t.Lit.Of(2L)));
            t.Root.Freeze();
            var child = t.Root.CreateChild();
            child.Assume(t.Add.Of(t.Lit.Of(1L), t.Lit.Of(2L)).Is(t.A.Of()));

            var variants = child.ExtractVariants(sum, 5);

            Assert.Equal(["(A)", "(Add (Lit 1) (Lit 2))"], variants.Select(v => v.ToString()));
            Assert.All(variants, v => Assert.Throws<ArgumentException>(() => t.Root.Find(new Node(v.Class))));
            Assert.All(variants, v => Assert.Equal(child.Find(sum), child.Find(new Node(v.Class))));
            Assert.Equal(["(Add (Lit 1) (Lit 2))"], t.Root.ExtractVariants(sum, 5).Select(v => v.ToString()));
            Assert.Equal(["(Add (Lit 1) (Lit 2))"], t.Root.CreateChild().ExtractVariants(sum, 5).Select(v => v.ToString()));
        }

        [Fact]
        public void AChildsUnionJoinsTheVariantsOfTwoParentClasses()
        {
            var t = new NodeTheory();
            var x = t.Root.Make(t.Wrap.Of(t.Lit.Of(1L)));
            var y = t.Root.Make(t.Add.Of(t.Lit.Of(1L), t.Lit.Of(2L)));
            var z = t.Root.Make(t.Wrap.Of(y));
            t.Root.Freeze();
            var child = t.Root.CreateChild();

            child.Union(y, x);

            string[] joined = ["(Wrap (Lit 1))", "(Add (Lit 1) (Lit 2))"];
            Assert.Equal(joined, child.ExtractVariants(x, 5).Select(v => v.ToString()));
            Assert.Equal(joined, child.ExtractVariants(y, 5).Select(v => v.ToString()));
            Assert.Equal(["(Wrap (Wrap (Lit 1)))"], child.ExtractVariants(z, 5).Select(v => v.ToString()));
            Assert.Equal(["(Wrap (Lit 1))"], t.Root.ExtractVariants(x, 5).Select(v => v.ToString()));
            Assert.Equal(["(Add (Lit 1) (Lit 2))"], t.Root.ExtractVariants(y, 5).Select(v => v.ToString()));
            Assert.Equal(["(Wrap (Add (Lit 1) (Lit 2)))"], t.Root.ExtractVariants(z, 5).Select(v => v.ToString()));
        }

        [Fact]
        public void AChildsSubsumptionHidesAVariantOnlyThere()
        {
            var t = new MathTheory();
            var one = t.Root.Make(t.Num.Of(1L));
            t.Root.Union(one, t.Root.Make(t.Num.Of(2L)));
            t.Root.Freeze();
            var child = t.Root.CreateChild();

            child.Subsume(t.Num.Of(1L));

            Assert.Equal(["(Num 2)"], child.ExtractVariants(one, 2).Select(v => v.ToString()));
            Assert.Equal(["(Num 1)", "(Num 2)"], t.Root.ExtractVariants(one, 2).Select(v => v.ToString()));
            Assert.Equal(["(Num 1)", "(Num 2)"], t.Root.CreateChild().ExtractVariants(one, 2).Select(v => v.ToString()));
        }

        [Fact]
        public void AParentChangeMakesAChildsExtractorRefuseVariants()
        {
            var t = new NodeTheory();
            var one = t.Root.Make(t.Lit.Of(1L));
            var child = t.Root.CreateChild();
            var extractor = child.CreateExtractor();
            Assert.Single(extractor.ExtractVariants(one, 5));

            t.Root.Union(one, t.Root.Make(t.B.Of()));

            Assert.Throws<InvalidOperationException>(() => extractor.ExtractVariants(one, 5));
            Assert.Equal(["(B)", "(Lit 1)"], child.ExtractVariants(one, 5).Select(v => v.ToString()));
        }

        [Fact]
        public void AFrozenScopesVariantsComeOnManyThreads()
        {
            var t = new NodeTheory();
            var terms = new Node[32];
            for (var i = 0; i < terms.Length; i++)
            {
                terms[i] = t.Root.Make(t.Wrap.Of(t.Lit.Of(i)));
                t.Root.Union(terms[i], t.Root.Make(t.Add.Of(t.Lit.Of(i), t.Lit.Of(i + 1L))));
            }

            t.Root.Freeze();
            var shared = t.Root.CreateExtractor();
            var rendered = new string[8][];
            var threads = DedicatedThreads.For(rendered.Length, rendered.Length, k =>
            {
                var own = t.Root.CreateExtractor();
                rendered[k] = new string[terms.Length];
                for (var i = 0; i < terms.Length; i++)
                {
                    var extractor = (k + i) % 2 == 0 ? own : shared;
                    rendered[k][i] = string.Join(" ", extractor.ExtractVariants(terms[i], 3).Select(v => v.ToString()));
                }
            });

            Assert.Equal(rendered.Length, threads);
            for (var i = 0; i < terms.Length; i++)
                Assert.All(rendered, r => Assert.Equal($"(Wrap (Lit {i})) (Add (Lit {i}) (Lit {i + 1}))", r[i]));
        }

    }

}
