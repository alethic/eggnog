using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for rule-layer decision D10: ties broken by cost, then function, then columns (classes by canonical id, base
    /// values by content), independent of insertion and union order; and acyclic results with zero costs.
    /// </summary>
    public class ExtractorTieBreakTests
    {

        [Fact]
        public void EqualCostNodesPreferTheEarlierDeclaredFunction()
        {
            foreach (var bFirst in new[] { false, true })
            {
                var fx = new ExtractionFixture();
                var e = new EqSort("E");
                var a = fx.Constructor("A", e, 1);
                var b = fx.Constructor("B", e, 1);
                var (first, second) = bFirst ? (fx.Make(b), fx.Make(a)) : (fx.Make(a), fx.Make(b));
                fx.Union(second, first);
                Assert.Equal(("(A)", 1L), fx.Extract(e, first));
            }
        }

        [Fact]
        public void EqualCostNodesPreferSmallerBaseValuesByContent()
        {
            // -5 is stored interned (its value has the top bit set), 3 inline: content order, not storage order.
            foreach (var reversed in new[] { false, true })
            {
                var fx = new ExtractionFixture();
                var e = new EqSort("E");
                var num = fx.Constructor("Num", e, 1, fx.I64);
                var values = reversed ? new long[] { 3, 9, -5 } : [-5, 9, 3];
                var ids = values.Select(v => fx.Make(num, fx.Int(v))).ToArray();
                fx.Union(ids[0], ids[1]);
                fx.Union(ids[1], ids[2]);
                Assert.True(fx.Int(-5).Rep > fx.Int(3).Rep);
                Assert.Equal(("(Num -5)", 2L), fx.Extract(e, ids[0]));
            }
        }

        [Fact]
        public void StringsTieByOrdinalContent()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var name = fx.Constructor("Name", e, 1, fx.String);
            var b = fx.Make(name, fx.Str("b"));
            var a = fx.Make(name, fx.Str("a"));
            fx.Union(b, a);
            Assert.Equal("(Name \"a\")", fx.Extract(e, b)?.Text);
        }

        [Fact]
        public void AGivenComparerOrdersBaseValues()
        {
            // A base sort may supply its own culture-invariant content order; here reversed ordinal order, so "b" wins.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var order = Comparer<string>.Create((x, y) => string.CompareOrdinal(y, x));
            var reversed = new BaseSort<StringCodec, string>("RevString", comparer: order);
            fx.AddBaseSort(reversed, fx.String);
            var name = fx.Constructor("Name", e, 1, reversed);
            var a = fx.Make(name, fx.Str("a"));
            var b = fx.Make(name, fx.Str("b"));
            fx.Union(a, b);
            Assert.Equal("(Name \"b\")", fx.Extract(e, a)?.Text);
        }

        [Fact]
        public void EqualCostChildrenTieBySmallerCanonicalId()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var leafX = fx.Constructor("X", e, 1);
            var leafY = fx.Constructor("Y", e, 1);
            var wrap = fx.Constructor("W", e, 1, e);
            var x = fx.Make(leafX);
            var y = fx.Make(leafY);
            var wy = fx.Make(wrap, y);
            var wx = fx.Make(wrap, x);
            fx.Union(wy, wx);
            Assert.True(x.Rep < y.Rep);
            Assert.Equal(("(W (X))", 2L), fx.Extract(e, wy));
        }

        [Fact]
        public void CheaperAlwaysBeatsEarlier()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var a = fx.Constructor("A", e, 5);
            var b = fx.Constructor("B", e, 4);
            fx.Union(fx.Make(a), fx.Make(b));
            Assert.Equal(("(B)", 4L), fx.Extract(e, fx.Get(a)));
        }

        [Fact]
        public void ZeroCostCyclesStillExtractFiniteTerms()
        {
            // E = { Wrap(E) :cost 0, Leaf :cost 0 }, and F = { Loop(F) } only: E extracts (Leaf), F has no term.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var f = new EqSort("F");
            var wrap = fx.Constructor("Wrap", e, 0, e);
            var leaf = fx.Constructor("Leaf", e, 0);
            var pair = fx.Constructor("Pair", e, 0, e, e);
            var loop = fx.Constructor("Loop", f, 0, f);
            var l = fx.Make(leaf);
            var w = fx.Make(wrap, l);
            fx.Union(w, l);
            var p = fx.Make(pair, l, l);
            fx.Union(p, l);

            Assert.Equal(("(Leaf)", 0L), fx.Extract(e, l));

            var seed = fx.EGraph.FreshId();
            var looped = fx.Make(loop, seed);
            fx.Union(looped, seed);
            Assert.Null(fx.Extract(f, looped));
        }

        [Fact]
        public void ZeroCostChainsChooseInFinalizationOrder()
        {
            // Every node costs 0, so every class ties at 0. The result must still be finite and every class a choice of
            // its own: A = { a0, f(B) }, B = { g(A) }.
            var fx = new ExtractionFixture();
            var s = new EqSort("S");
            var a0 = fx.Constructor("a0", s, 0);
            var f = fx.Constructor("f", s, 0, s);
            var g = fx.Constructor("g", s, 0, s);
            var a = fx.Make(a0);
            var b = fx.Make(g, a);
            fx.Union(fx.Make(f, b), a);

            var extractor = Extractor.ComputeCostsFromRootSorts(fx.Schema, [s]);
            Assert.True(extractor.TryGetChoice(s, a, out var fa, out _, out var rankA));
            Assert.True(extractor.TryGetChoice(s, b, out var fb, out _, out var rankB));
            Assert.Equal(a0, fa);
            Assert.Equal(g, fb);
            Assert.True(rankA < rankB);
            Assert.Equal("(g (a0))", fx.Extract(s, b)?.Text);
        }

        [Fact]
        public void TheResultDoesNotDependOnInsertionOrder()
        {
            // The same e-graph contents built in different orders get different ids, so compare the structure: with
            // distinct costs and content ties only on base values, the extracted text must agree.
            string Run(int seed)
            {
                var random = new Random(seed);
                var fx = new ExtractionFixture();
                var e = new EqSort("E");
                var num = fx.Constructor("Num", e, 1, fx.I64);
                var neg = fx.Constructor("Neg", e, 1, e);
                var add = fx.Constructor("Add", e, 1, e, e);
                var numbers = Enumerable.Range(0, 6).OrderBy(_ => random.Next()).ToArray();
                var ids = numbers.ToDictionary(n => n, n => fx.Make(num, fx.Int(n)));
                foreach (var n in numbers.OrderBy(_ => random.Next()))
                    fx.Union(ids[n], ids[(n + 1) % 6]);

                var sum = fx.Make(add, ids[0], fx.Make(neg, ids[3]));
                return fx.Extract(e, sum)!.Value.Text;
            }

            var expected = Run(0);
            Assert.Equal("(Add (Num 0) (Neg (Num 0)))", expected);
            for (var seed = 1; seed < 10; seed++)
                Assert.Equal(expected, Run(seed));
        }

    }

}
