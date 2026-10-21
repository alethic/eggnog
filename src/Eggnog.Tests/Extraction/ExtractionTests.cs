using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Extraction
{

    /// <summary>
    /// Extraction: costs, ties, unextractable heads, sharing, staleness, the extracted term's API, and the classes and
    /// literals an extracted term holds.
    /// </summary>
    public sealed class ExtractionTests
    {

        [Fact]
        public void AClassWithNoFiniteTermHasNoExtraction()
        {
            var t = new NodeTheory();
            var hidden = t.Graph.DefineConstructor<Node>("Hidden", options: new TableOptions { Extractable = false });
            var h = t.Root.Make(hidden.Of());
            var extractor = t.Root.CreateExtractor();
            Assert.False(extractor.TryExtract(h, out var term));
            Assert.Null(term);
            Assert.False(extractor.TryGetCost(h, out _));
            Assert.Throws<InvalidOperationException>(() => extractor.Extract(h));
            Assert.Throws<InvalidOperationException>(() => t.Root.Extract(h));
        }

        [Fact]
        public void ZeroCostNodesStillCountTheirChildren()
        {
            var t = new NodeTheory();
            var free = t.Graph.DefineConstructor<Node, Node>("Free", cost: 0);
            var term = t.Root.Extract(t.Root.Make(free.Of(t.Lit.Of(1L))));
            Assert.Equal<ulong>(2, term.Cost);
            Assert.Equal("(Free (Lit 1))", term.ToString());
            Assert.Equal<ulong>(2, term.Child(0).Cost);
        }

        [Fact]
        public void TiesKeepTheSpellingAsWritten()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("commute", r =>
            {
                var (a, b) = r.Vars<Node, Node>("a", "b");
                r.Rewrite(t.Add.Of(a, b), t.Add.Of(b, a));
            });
            var sum = t.Root.Make(t.Add.Of(t.Lit.Of(2L), t.Lit.Of(1L)));
            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.AreEqual(sum, t.Add.Of(t.Lit.Of(1L), t.Lit.Of(2L))));
            Assert.Equal("(Add (Lit 2) (Lit 1))", t.Root.Extract(sum).ToString());
        }

        [Fact]
        public void TheCheapestNodeWins()
        {
            var t = new NodeTheory();
            var slow = t.Graph.DefineConstructor<long, Node>("Slow", cost: 100);
            var s = t.Root.Make(slow.Of(1L));
            t.Root.Union(s, t.Root.Make(t.Lit.Of(1L)));
            var term = t.Root.Extract(s);
            Assert.Equal("(Lit 1)", term.ToString());
            Assert.Equal<ulong>(2, term.Cost);
            Assert.True(t.Root.CreateExtractor().TryGetCost(s, out var cost));
            Assert.Equal<ulong>(2, cost);
        }

        [Fact]
        public void PerRowCostsComeFromTheCostFunction()
        {
            var t = new NodeTheory();
            var slow = t.Graph.DefineConstructor<long, Node>("Slow", cost: 100);
            var s = t.Root.Make(slow.Of(3L));
            t.Root.Union(s, t.Root.Make(t.Lit.Of(5L)));
            var heads = new System.Collections.Generic.List<string>();
            var costs = CostModel.Default.With(t.Lit, node =>
            {
                heads.Add(node.Head.Name);
                Assert.Equal(1, node.Arity);
                Assert.Equal(5L, (long)node.Get(0));
                return (ulong)(node.Get<long>(0) * 100);
            });

            var term = t.Root.Extract(s, costs);

            Assert.Equal("(Slow 3)", term.ToString());
            Assert.Equal<ulong>(101, term.Cost);
            Assert.Contains("Lit", heads);
            Assert.Equal("(Lit 5)", t.Root.Extract(s, CostModel.Default.With(slow, 1000)).ToString());
        }

        [Fact]
        public void UnextractableHeadsAreSkipped()
        {
            var t = new NodeTheory();
            var fast = t.Graph.DefineConstructor<long, Node>("Fast", cost: 0, options: new TableOptions { Extractable = false });
            var slow = t.Graph.DefineConstructor<long, Node>("Slow", cost: 10);
            var f = t.Root.Make(fast.Of(1L));
            t.Root.Union(f, t.Root.Make(slow.Of(1L)));
            t.Root.Union(f, t.Root.Make(t.Lit.Of(1L)));

            Assert.Equal("(Lit 1)", t.Root.Extract(f).ToString());
            Assert.Equal("(Slow 1)", t.Root.Extract(f, CostModel.Default.Unextractable(t.Lit)).ToString());
            Assert.False(t.Root.CreateExtractor(CostModel.Default.Unextractable(t.Lit).Unextractable(slow)).TryExtract(f, out _));
        }

        [Fact]
        public void ValueCostsChangeTheTotal()
        {
            var t = new NodeTheory();
            var named = t.Graph.DefineConstructor<string, Node>("Named");
            var lit = t.Root.Make(t.Lit.Of(7L));
            var name = t.Root.Make(named.Of("seven"));

            Assert.Equal<ulong>(2, t.Root.Extract(lit).Cost);
            Assert.Equal<ulong>(101, t.Root.Extract(lit, CostModel.Default.WithValueCost(t.Graph.Values.TypeOf<long>(), 100)).Cost);
            Assert.Equal<ulong>(6, t.Root.Extract(name, CostModel.Default.WithValueCost(t.Graph.Values.TypeOf<string>(), s => (ulong)s.Length)).Cost);
            Assert.Throws<ArgumentException>(() => CostModel.Default.WithValueCost(t.Graph.Values.TypeOf<Node>(), 1));
            Assert.ThrowsAny<InvalidOperationException>(() => t.Root.Extract(lit, CostModel.Default.With(t.Lit, _ => throw new InvalidOperationException("no cost"))));
        }

        [Fact]
        public void SharedSubtermsAreOneInstance()
        {
            var t = new NodeTheory();
            var sum = t.Root.Make(t.Add.Of(t.Lit.Of(1L), t.Lit.Of(1L)));
            var term = t.Root.Extract(sum);
            Assert.Same(term.Child(0), term.Child(1));
            Assert.Equal<ulong>(5, term.Cost);
            Assert.Equal("(Add (Lit 1) (Lit 1))", term.ToString());
        }

        [Fact]
        public void AStaleExtractorThrowsAfterAChangingWrite()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var extractor = t.Root.CreateExtractor();
            Assert.Equal("(A)", extractor.Extract(a).ToString());

            // A write to a table extraction does not read keeps the extractor current; a term does not.
            t.Root.Assume(t.Num.Of(1L));
            Assert.Equal("(A)", extractor.Extract(a).ToString());
            t.Root.Make(t.B.Of());
            Assert.Contains("changed", Assert.Throws<InvalidOperationException>(() => extractor.Extract(a)).Message);
            Assert.Equal("(A)", t.Root.CreateExtractor().Extract(a).ToString());
        }

        [Fact]
        public void TheTermKnowsItsHeadClassAndArguments()
        {
            var t = new NodeTheory();
            var sum = t.Root.Make(t.Add.Of(t.Lit.Of(1L), t.A.Of()));
            var term = t.Root.Extract(sum);
            Assert.Same(t.Add, term.Head);
            Assert.True(term.Is(t.Add));
            Assert.False(term.Is(t.Lit));
            Assert.Equal(t.Root.Find(sum).Id, term.Class);
            Assert.Equal(2, term.Arity);
            Assert.True(term.IsChild(0));
            var lit = term.Child(0);
            Assert.Same(lit, term.Argument(0));
            Assert.False(lit.IsChild(0));
            Assert.Equal(1L, lit.Value<long>(0));
            Assert.Equal(1L, lit.Argument(0));
            Assert.Equal(t.Root.Get(t.Lit.Of(1L)), term.Value<Node>(0));
            Assert.Throws<InvalidOperationException>(() => lit.Child(0));
            Assert.Throws<ArgumentException>(() => lit.Value<string>(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => lit.Argument(1));
            Assert.True(term.Mentions(t.Lit));
            Assert.True(term.Mentions(t.A));
            Assert.True(term.Mentions(t.Add));
            Assert.False(term.Mentions(t.Wrap));
        }

        [Fact]
        public void TryMatchBindsArgumentsAtEveryArity()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var zero = graph.DefineConstructor<Node>("Zero");
            var one = graph.DefineConstructor<long, Node>("One");
            var two = graph.DefineConstructor<string, Node, Node>("Two");
            var three = graph.DefineConstructor<long, Node, bool, Node>("Three");
            var four = graph.DefineConstructor<Node, long, string, double, Node>("Four");
            var root = graph.Root;
            var node = root.Make(four.Of(three.Of(1L, two.Of("x", one.Of(2L)), true), 3L, "y", 1.5));
            var z = root.Make(zero.Of());
            var term = root.Extract(node);

            Assert.False(term.TryMatch(zero));
            Assert.False(term.TryMatch(one, out _));
            Assert.True(term.TryMatch(four, out var a, out var b, out var c, out var d));
            Assert.Equal(3L, b.Value);
            Assert.Null(b.Term);
            Assert.Equal("y", c.Value);
            Assert.Equal(1.5, d.Value);
            Assert.NotNull(a.Term);
            Assert.Equal(a.Term!.Class, a.Value.Id);
            Assert.Equal("3", b.ToString());

            Assert.True(a.Term.TryMatch(three, out var x, out var y, out var flag));
            Assert.Equal(1L, x.Value);
            Assert.True(flag.Value);
            Assert.True(y.Term!.TryMatch(two, out var text, out var child));
            Assert.Equal("x", text.Value);
            Assert.True(child.Term!.TryMatch(one, out var number));
            Assert.Equal(2L, number.Value);
            Assert.True(root.Extract(z).TryMatch(zero));
            Assert.False(term.TryMatch(two, out var none, out _));
            Assert.Null(none.Value);
        }

        [Fact]
        public void LiteralsRenderAsEgglogPrintsThem()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var values = graph.DefineConstructor<long, int, bool, double, Node>("V");
            var text = graph.DefineConstructor<string, Node>("T");
            var sets = graph.DefineConstructor<Set<string>, Set<long>, Set<int>, Node>("S");
            var root = graph.Root;

            // As printed by the egglog CLI built from ext/egglog at 5e655cae: (FL -42), (FB true), (FD 1.5), (FD 2.0),
            // (FT "kind"), (FS (set-of "a" "b")), (FS (set-of)).
            Assert.Equal("(V -42 7 true 1.5)", root.Extract(root.Make(values.Of(-42L, 7, true, 1.5))).ToString());
            Assert.Equal("(V 42 -1 false 2.0)", root.Extract(root.Make(values.Of(42L, -1, false, 2.0))).ToString());
            Assert.Equal("(T \"kind\")", root.Extract(root.Make(text.Of("kind"))).ToString());
            Assert.Equal("(S (set-of \"a\" \"b\") (set-of 1 3) (set-of))",
                root.Extract(root.Make(sets.Of(Set.Of("b", "a"), Set.Of(3L, 1L), Set<int>.Empty))).ToString());
        }

        /// <summary>
        /// Enum values extract and render as their member names.
        /// </summary>
        [Fact]
        public void EnumsExtractByMemberName()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            graph.Values.RegisterEnum<Shade>();
            var e = graph.DefineConstructor<Shade, Node>("E");
            var root = graph.Root;
            Assert.Equal("(E Dark)", root.Extract(root.Make(e.Of(Shade.Dark))).ToString());
        }

        /// <summary>
        /// The classes of an extracted term are canonical in the extractor's scope: after the union of <c>B</c> and
        /// <c>A</c>, the term's child, the argument <c>TryMatch</c> binds and the child's <c>Value</c> all name the
        /// canonical class.
        /// </summary>
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

        /// <summary>
        /// A value cost set for <c>int</c> applies to <c>int</c> cells only, not to <c>long</c> ones.
        /// </summary>
        [Fact]
        public void IntAndLongValueCostsAreSeparate()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var both = graph.DefineConstructor<int, long, Node>("Both");
            var n = graph.Root.Make(both.Of(1, 2L));
            var costs = CostModel.Default.WithValueCost(graph.Values.TypeOf<int>(), 10);
            Assert.Equal<ulong>(12, graph.Root.Extract(n, costs).Cost);
            Assert.Equal<ulong>(3, graph.Root.Extract(n).Cost);
        }

        /// <summary>
        /// An extractor of the root keeps working across root writes it does not read, and the root's
        /// <see cref="Scope.Version"/> moves only with those writes: the root has no ancestor to catch up with.
        /// </summary>
        [Fact]
        public void TheRootsExtractorSurvivesWritesItDoesNotRead()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var extractor = t.Root.CreateExtractor();
            Assert.Equal("(A)", extractor.Extract(a).ToString());
            var version = t.Root.Version;
            Assert.Equal("(A)", extractor.Extract(a).ToString());
            Assert.Equal(version, t.Root.Version);
            t.Root.Assume(t.Tag.Of(a));
            Assert.Equal("(A)", extractor.Extract(a).ToString());
        }

    }

}
