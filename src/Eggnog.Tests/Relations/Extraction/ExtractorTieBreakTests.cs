using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests that the extractor breaks ties as egglog's does: a class extracts as the first row, functions in egglog's
    /// breadth-first order and each function's rows in storage order, that costs the class's cost and whose children
    /// all reached their costs before the class did; and variants sort by cost, function name and raw row values. Each
    /// test gives the egglog program it mirrors, and expects what egglog prints for it at the engine's commit.
    /// </summary>
    public class ExtractorTieBreakTests
    {

        [Fact]
        public void EqualCostNodesTakeTheFirstFunctionDeclared()
        {
            // (datatype E (Bee :cost 1) (Ant :cost 1)) (let b (Bee)) (let a (Ant)) (union a b) (extract a) (extract a 5)
            // prints (Bee), then (Ant) and (Bee): the term follows declaration order, the variants follow names.
            foreach (var antFirst in new[] { false, true })
            {
                var fx = new ExtractionFixture();
                var e = new EqSort("E");
                var bee = fx.Constructor("Bee", e, 1);
                var ant = fx.Constructor("Ant", e, 1);
                var (first, second) = antFirst ? (fx.Make(ant), fx.Make(bee)) : (fx.Make(bee), fx.Make(ant));
                fx.Union(second, first);
                Assert.Equal(("(Bee)", 1L), fx.Extract(e, first));
                Assert.Equal(["(Ant)", "(Bee)"], Variants(fx, e, first, 5));
            }
        }

        [Fact]
        public void EqualCostRowsOfOneFunctionTakeTheFirstStored()
        {
            // (datatype E (Num i64)) (let x0 (Num -5)) (let x1 (Num 9)) (let x2 (Num 3)) (union x0 x1) (union x1 x2)
            // (extract x0) prints (Num -5), and (Num 3) with the values reversed; the variants sort by raw value, and
            // -5 is stored interned (its value has the top bit set) while 3 and 9 are inline.
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
                Assert.Equal((reversed ? "(Num 3)" : "(Num -5)", 2L), fx.Extract(e, ids[0]));
                Assert.Equal(["(Num 3)", "(Num 9)", "(Num -5)"], Variants(fx, e, ids[0], 5));
            }
        }

        [Fact]
        public void StringsTieByStorageNotContent()
        {
            // (datatype E (Name String)) (let b (Name "b")) (let a (Name "a")) (union b a) (extract b) (extract b 5)
            // prints (Name "b"), then (Name "b") and (Name "a"): "b" was interned first.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var name = fx.Constructor("Name", e, 1, fx.String);
            var b = fx.Make(name, fx.Str("b"));
            var a = fx.Make(name, fx.Str("a"));
            fx.Union(b, a);
            Assert.Equal("(Name \"b\")", fx.Extract(e, b)?.Text);
            Assert.Equal(["(Name \"b\")", "(Name \"a\")"], Variants(fx, e, b, 5));
        }

        [Fact]
        public void EqualCostChildrenTakeTheFirstStoredRow()
        {
            // (datatype E (X) (Y) (W E)) (let x (X)) (let y (Y)) (let wy (W y)) (let wx (W x)) (union wy wx)
            // (extract wy) (extract wy 5) prints (W (Y)), then (W (X)) and (W (Y)): x has the smaller id.
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
            Assert.Equal(("(W (Y))", 2L), fx.Extract(e, wy));
            Assert.Equal(["(W (X))", "(W (Y))"], Variants(fx, e, wy, 5));
        }

        [Fact]
        public void ARowWhoseChildReachedItsCostLaterIsPassedOver()
        {
            // (datatype E (F E :cost 0) (K :cost 2) (G E :cost 0) (M :cost 2)) (let k (K)) (let fd (F (M)))
            // (union k fd) (let gc (G k)) (union gc (M)) (extract k) (extract (M)) (extract k 5) prints (K), (G (K)),
            // then (F (G (K))) and (K). The first pass sets k's class by (K), then (M)'s by (G k), so (F (M)) costs
            // as much as (K) but its child reached its cost after k's class did.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var f = fx.Constructor("F", e, 0, e);
            var k = fx.Constructor("K", e, 2);
            var g = fx.Constructor("G", e, 0, e);
            var m = fx.Constructor("M", e, 2);
            var kId = fx.Make(k);
            fx.Union(kId, fx.Make(f, fx.Make(m)));
            fx.Union(fx.Make(g, kId), fx.Make(m));
            Assert.Equal(("(K)", 2L), fx.Extract(e, kId));
            Assert.Equal(("(G (K))", 2L), fx.Extract(e, fx.Make(m)));
            Assert.Equal(["(F (G (K)))", "(K)"], Variants(fx, e, kId, 5));
        }

        [Fact]
        public void EachQueryTakesTheOrderOfItsSort()
        {
            // (sort A) (sort B) (constructor F (B) A :cost 0) (constructor L () B :cost 1) (constructor G () A :cost 1)
            // (constructor W (A) B :cost 0) (let b (L)) (let c (F b)) (union c (G)) (let x (W c)) (extract c) (extract x)
            // prints (G) and (W (F (L))): rooted at A the passes meet F, G, L, W, so (G) sets c's class before (L)
            // sets b's; rooted at B they meet L, W, F, G, so (F b) sets it after. One on-demand extractor answers both.
            var fx = new ExtractionFixture();
            var a = new EqSort("A");
            var b = new EqSort("B");
            var f = fx.Constructor("F", a, 0, b);
            var l = fx.Constructor("L", b, 1);
            var g = fx.Constructor("G", a, 1);
            var w = fx.Constructor("W", b, 0, a);
            var bId = fx.Make(l);
            var c = fx.Make(f, bId);
            fx.Union(c, fx.Make(g));
            c = fx.Canon(c);
            var x = fx.Make(w, c);

            Assert.Equal(("(G)", 1L), fx.Extract(a, c));
            Assert.Equal(("(W (F (L)))", 1L), fx.Extract(b, x));
            var onDemand = Extractor.CreateOnDemand(fx.Schema);
            Assert.Equal("(W (F (L)))", onDemand.ExtractTerm(b, x).ToString());
            Assert.Equal("(G)", onDemand.ExtractTerm(a, c).ToString());
            Assert.Equal("(W (F (L)))", onDemand.ExtractTerm(b, x).ToString());
        }

        [Fact]
        public void ASaturatedClassStillExtracts()
        {
            // (datatype E (F E :cost 9223372036854775807) (H E :cost 1) (K :cost 100) (N E :cost 1) (Q :cost 1))
            // (let k (K)) (let c (F k)) (let q (Q)) (let m (N q)) (union k (H m)) (extract c) prints
            // (F (H (N (Q)))). egglog's u64 costs keep c's cost exact, so its last update follows k's; here it saturates
            // at long.MaxValue on its first update, before k's last, and takes the fallback: the first row whose child
            // had its first update before c had its own.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var f = fx.Constructor("F", e, long.MaxValue, e);
            var h = fx.Constructor("H", e, 1, e);
            var kHead = fx.Constructor("K", e, 100);
            var n = fx.Constructor("N", e, 1, e);
            var q = fx.Constructor("Q", e, 1);
            var k = fx.Make(kHead);
            var c = fx.Make(f, k);
            var m = fx.Make(n, fx.Make(q));
            fx.Union(k, fx.Make(h, m));

            Assert.Equal(("(F (H (N (Q))))", long.MaxValue), fx.Extract(e, c));
            Assert.Equal("(F (H (N (Q))))", Extractor.CreateOnDemand(fx.Schema).ExtractTerm(e, c).ToString());
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
            // (datatype E (Wrap E :cost 0) (Leaf :cost 0) (Pair E E :cost 0)) (let l (Leaf)) (let w (Wrap l))
            // (union w l) (let p (Pair l l)) (union p l) (extract l) (extract l 5) prints (Leaf), then (Leaf),
            // (Pair (Leaf) (Leaf)) and (Wrap (Leaf)). F = { Loop(F) } has no term.
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
            Assert.Equal(["(Leaf)", "(Pair (Leaf) (Leaf))", "(Wrap (Leaf))"], Variants(fx, e, l, 5));

            var seed = fx.EGraph.FreshId();
            var looped = fx.Make(loop, seed);
            fx.Union(looped, seed);
            Assert.Null(fx.Extract(f, looped));
        }

        [Fact]
        public void ZeroCostChainsChooseInUpdateOrder()
        {
            // (datatype S (a0 :cost 0) (f S :cost 0) (g S :cost 0)) (let a (a0)) (let b (g a)) (union (f b) a)
            // (extract a) (extract b) (extract a 5) prints (a0), (g (a0)), then (a0) and (f (g (a0))), and so with f
            // and g declared before a0: every class ties at 0, and each still extracts a finite term.
            foreach (var leafLast in new[] { false, true })
            {
                var fx = new ExtractionFixture();
                var s = new EqSort("S");
                var a0 = leafLast ? default : fx.Constructor("a0", s, 0);
                var f = fx.Constructor("f", s, 0, s);
                var g = fx.Constructor("g", s, 0, s);
                if (leafLast)
                    a0 = fx.Constructor("a0", s, 0);

                var a = fx.Make(a0);
                var b = fx.Make(g, a);
                fx.Union(fx.Make(f, b), a);

                var extractor = Extractor.ComputeCostsFromRootSorts(fx.Schema, [s]);
                Assert.True(extractor.TryGetChoice(s, a, out var fa, out _));
                Assert.True(extractor.TryGetChoice(s, b, out var fb, out _));
                Assert.Equal(a0, fa);
                Assert.Equal(g, fb);
                Assert.Equal("(g (a0))", fx.Extract(s, b)?.Text);
                Assert.Equal(["(a0)", "(f (g (a0)))"], Variants(fx, s, a, 5));
            }
        }

        /// <summary>
        /// Renders the variants of a class, as egglog's <c>(extract x n)</c> lists them.
        /// </summary>
        static string[] Variants(ExtractionFixture fx, EqSort sort, Value value, int count)
        {
            var extractor = Extractor.ComputeCostsFromRootSorts(fx.Schema, [sort]);
            var dag = new TermDag();
            return extractor.ExtractVariants(sort, value, count, dag).Select(v => dag.ToString(v.Term)).ToArray();
        }

    }

}
