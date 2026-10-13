using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// egglog's extraction tests (tests/integration_test.rs:74-680, and src/lib.rs:2990-3087), ported from egglog
    /// programs to the bridge API: each <c>datatype</c> becomes constructors, each <c>let</c> a term, each
    /// <c>union</c>/<c>subsume</c> the same operation, and a rewrite run is replaced by the rows and unions it would
    /// add.
    /// </summary>
    public class ExtractorTests
    {

        [Fact]
        public void SimpleExtract1()
        {
            // (datatype Op (Add i64 i64)) (let expr (Add 1 1)) (extract expr)
            var fx = new ExtractionFixture();
            var op = new EqSort("Op");
            var add = fx.Constructor("Add", op, null, fx.I64, fx.I64);
            var expr = fx.Make(add, fx.Int(1), fx.Int(1));

            var dag = new TermDag();
            var result = Extractor.ExtractValue(fx.Schema, op, expr, dag);
            Assert.Equal(3, result.Cost);
            Assert.Equal("(Add 1 1)", dag.ToString(result.Term));
        }

        static (ExtractionFixture Fx, EqSort Term, FunctionId BigStep, FunctionId SmallStep, Value Tb) StepGraph()
        {
            // (datatype Term (Origin :cost 0) (BigStep Term :cost 10) (SmallStep Term :cost 1))
            var fx = new ExtractionFixture();
            var term = new EqSort("Term");
            var origin = fx.Constructor("Origin", term, 0);
            var bigStep = fx.Constructor("BigStep", term, 10, term);
            var smallStep = fx.Constructor("SmallStep", term, 1, term);
            var t = fx.Make(origin);
            var tb = fx.Make(bigStep, t);
            var tbs = fx.Make(smallStep, tb);
            var ts = fx.Make(smallStep, t);
            var tss = fx.Make(smallStep, ts);
            var tsss = fx.Make(smallStep, tss);
            fx.Union(tbs, tsss);
            var tssss = fx.Make(smallStep, tsss);
            fx.Union(tssss, tb);
            return (fx, term, bigStep, smallStep, tb);
        }

        [Fact]
        public void SimpleExtract2()
        {
            var (fx, term, _, _, tb) = StepGraph();
            var extracted = fx.Extract(term, tb);
            Assert.NotNull(extracted);
            Assert.Equal(4, extracted.Value.Cost);
            Assert.Equal("(SmallStep (SmallStep (SmallStep (SmallStep (Origin)))))", extracted.Value.Text);
        }

        [Fact]
        public void SimpleExtract3()
        {
            // (datatype Fruit (Apple i64 :cost 1) (Orange f64 :cost 2))
            // (datatype Vegetable (Broccoli bool :cost 3) (Carrot Fruit :cost 4))
            var fx = new ExtractionFixture();
            var fruit = new EqSort("Fruit");
            var vegetable = new EqSort("Vegetable");
            var apple = fx.Constructor("Apple", fruit, 1, fx.I64);
            var orange = fx.Constructor("Orange", fruit, 2, fx.F64);
            var broccoli = fx.Constructor("Broccoli", vegetable, 3, fx.Bool);
            var carrot = fx.Constructor("Carrot", vegetable, 4, fruit);
            var a = fx.Make(apple, fx.Int(5));
            var o = fx.Make(orange, fx.Float(3.14));
            var b = fx.Make(broccoli, fx.Boolean(true));
            var c = fx.Make(carrot, a);

            Assert.Equal(("(Apple 5)", 2L), fx.Extract(fruit, a));
            Assert.Equal(("(Orange 3.14)", 3L), fx.Extract(fruit, o));
            Assert.Equal(("(Broccoli true)", 4L), fx.Extract(vegetable, b));
            Assert.Equal(("(Carrot (Apple 5))", 6L), fx.Extract(vegetable, c));
        }

        [Fact]
        public void SimpleExtract4()
        {
            // A subsumed constructor leaves its class, and so its parent, with no term: egglog's extract errors.
            var fx = new ExtractionFixture();
            var foo = new EqSort("Foo");
            var bar = new EqSort("Bar");
            var foobar = fx.Constructor("Foobar", foo, null);
            fx.Make(foobar);
            fx.Subsume(foobar);
            var barbar = fx.Constructor("Barbar", bar, null, foo);
            var x = fx.Make(barbar, fx.Get(foobar));

            Assert.Null(fx.Extract(bar, x));
            Assert.Throws<InvalidOperationException>(() => Extractor.ExtractValue(fx.Schema, bar, x, new TermDag()));
        }

        [Fact]
        public void SimpleExtract5()
        {
            // (sort QuaVec (Vec i64)) (sort QuaMap (Map QuaVec Foo)) (function Quaz () QuaMap :no-merge)
            // (set (Quaz) (map-empty)) (extract (Quaz)): an empty container costs nothing.
            var fx = new ExtractionFixture();
            var foo = new EqSort("Foo");
            var bar = new EqSort("Bar");
            var foobar = fx.Constructor("Foobar", foo, null, fx.I64);
            fx.Make(foobar, fx.Int(42));
            var barfoo = fx.Constructor("Barfoo", bar, null, fx.I64);
            fx.Make(barfoo, fx.Int(24));
            var quaVec = new TestVecSort("QuaVec", fx.I64);
            var quaMap = new TestMapSort("QuaMap", quaVec, foo);
            var quaz = fx.Function("Quaz", quaMap);
            fx.Set(quaz, [], fx.EGraph.GetContainerValue(new MapContainer(true)));

            Assert.Equal(("(map-empty)", 0L), fx.Extract(quaMap, fx.Get(quaz)));
        }

        [Fact]
        public void SimpleExtract6()
        {
            // As above, with a map into a sort that has no constructors at all.
            var fx = new ExtractionFixture();
            var @false = new EqSort("False");
            var quaVec = new TestVecSort("QuaVec", fx.I64);
            var quaMap = new TestMapSort("QuaMap", quaVec, @false);
            var quaz = fx.Function("Quaz", quaMap);
            fx.Set(quaz, [], fx.EGraph.GetContainerValue(new MapContainer(true)));

            Assert.Equal(("(map-empty)", 0L), fx.Extract(quaMap, fx.Get(quaz)));
        }

        [Fact]
        public void SimpleExtract7()
        {
            // (datatype Foo (bar) (baz)) (sort Mapsrt1 (Map i64 Foo)) (let map1 (map-insert (map-empty) 0 (bar)))
            // (sort Mapsrt2 (Map bool Foo)) (let map2 (map-insert (map-empty) false (baz))) (union (bar) (baz))
            var fx = new ExtractionFixture();
            var foo = new EqSort("Foo");
            var bar = fx.Constructor("bar", foo, null);
            var baz = fx.Constructor("baz", foo, null);
            var mapsrt1 = new TestMapSort("Mapsrt1", fx.I64, foo);
            var mapsrt2 = new TestMapSort("Mapsrt2", fx.Bool, foo);
            var map1 = fx.Function("map1", mapsrt1);
            var map2 = fx.Function("map2", mapsrt2);
            var barId = fx.Make(bar);
            var bazId = fx.Make(baz);
            fx.Set(map1, [], fx.EGraph.GetContainerValue(new MapContainer(true, (fx.Int(0).Rep, barId.Rep))));
            fx.Set(map2, [], fx.EGraph.GetContainerValue(new MapContainer(true, (fx.Boolean(false).Rep, bazId.Rep))));
            fx.Union(barId, bazId);

            // bar and baz tie at cost 1, and a tie goes to the function declared first: bar.
            Assert.Equal(("(map-insert (map-empty) 0 (bar))", 2L), fx.Extract(mapsrt1, fx.Get(map1)));
            Assert.Equal(("(map-insert (map-empty) false (bar))", 2L), fx.Extract(mapsrt2, fx.Get(map2)));
        }

        [Fact]
        public void SimpleExtract8()
        {
            // A custom function's rows are never extracted from: (function func () Foo :no-merge) (set (func) (bar)).
            var fx = new ExtractionFixture();
            var foo = new EqSort("Foo");
            var bar = fx.Constructor("bar", foo, 10);
            var func = fx.Function("func", foo);
            var barId = fx.Make(bar);
            fx.Set(func, [], barId);

            Assert.Equal(("(bar)", 10L), fx.Extract(foo, fx.Get(func)));
        }

        [Fact]
        public void SimpleExtract9()
        {
            // Costs saturate: bar costs i64::MAX, so everything built on it costs the maximum, and the cycle
            // a -> b -> c -> a never yields a cheaper term.
            var fx = new ExtractionFixture();
            var foo = new EqSort("Foo");
            var nodeA = new EqSort("NodeA");
            var nodeB = new EqSort("NodeB");
            var nodeC = new EqSort("NodeC");
            var ctoa = fx.Constructor("ctoa", nodeA, null, nodeC);
            var atob = fx.Constructor("atob", nodeB, null, nodeA);
            var btoc = fx.Constructor("btoc", nodeC, null, nodeB);
            var bar = fx.Constructor("bar", foo, long.MaxValue);
            var barbar = fx.Constructor("barbar", foo, 2, foo, foo);
            var groundedA = fx.Constructor("groundedA", nodeA, null, foo);

            var barId = fx.Make(bar);
            var a = fx.Make(groundedA, fx.Make(barbar, barId, barId));
            var b = fx.Make(atob, a);
            var c = fx.Make(btoc, b);
            var a2 = fx.Make(ctoa, c);
            var b2 = fx.Make(atob, a2);
            var c2 = fx.Make(btoc, b2);
            fx.Union(a, a2);
            fx.Union(b, b2);
            fx.Union(c, c2);

            var extractor = Extractor.ComputeCostsFromRootSorts(fx.Schema, [nodeA]);
            foreach (var (sort, value) in new (Sort, Value)[] { (nodeA, a), (nodeB, b), (nodeC, c) })
            {
                Assert.True(extractor.TryGetCost(sort, value, out var cost));
                Assert.Equal(long.MaxValue, cost);
            }

            Assert.Equal(("(groundedA (barbar (bar) (bar)))", long.MaxValue), fx.Extract(nodeA, a));
        }

        [Fact]
        public void ExtractVariants1()
        {
            var (fx, term, _, _, tb) = StepGraph();
            var extractor = Extractor.ComputeCostsFromRootSorts(fx.Schema, [term]);
            var dag = new TermDag();
            var variants = extractor.ExtractVariants(tb, 3, dag);

            Assert.Equal(
                ["(SmallStep (SmallStep (SmallStep (SmallStep (Origin)))))", "(BigStep (Origin))"],
                variants.Select(v => dag.ToString(v.Term)).ToArray());
            Assert.Equal([4L, 10L], variants.Select(v => v.Cost).ToArray());
        }

        [Fact]
        public void SubsumedUnextractableActionExtract()
        {
            // A subsumed row is not extracted, even when it is the cheapest.
            var fx = new ExtractionFixture();
            var math = new EqSort("Math");
            var expensive = fx.Constructor("expensive", math, 100);
            var cheap = fx.Constructor("cheap", math, 1);
            fx.Union(fx.Make(expensive), fx.Make(cheap));
            Assert.Equal("(cheap)", fx.Extract(math, fx.Get(expensive))?.Text);

            fx.Subsume(cheap);
            Assert.Equal("(expensive)", fx.Extract(math, fx.Get(expensive))?.Text);
        }

        [Fact]
        public void SubsumeUnextractableInsertAndMerge()
        {
            // (f (Num 1)) (subsume (f (Num 1))) (f (Num 2)) (union (Num 2) (Num 1)) (union (f (Num 2)) (expensive)):
            // merging the two f rows keeps the subsumption, so only expensive is left.
            var fx = new ExtractionFixture();
            var expr = new EqSort("Expr");
            var f = fx.Constructor("f", expr, null, expr);
            var num = fx.Constructor("Num", expr, null, fx.I64);
            var expensive = fx.Constructor("expensive", expr, 100);
            var num1 = fx.Make(num, fx.Int(1));
            fx.Make(f, num1);
            fx.Subsume(f, num1);
            var num2 = fx.Make(num, fx.Int(2));
            var f2 = fx.Make(f, num2);
            fx.Union(num2, num1);
            fx.Union(f2, fx.Make(expensive));

            Assert.Equal("(expensive)", fx.Extract(expr, fx.Canon(f2))?.Text);
        }

        [Fact]
        public void SubsumeUnextractableActionExtractMultiple()
        {
            var fx = new ExtractionFixture();
            var math = new EqSort("Math");
            var num = fx.Constructor("Num", math, null, fx.I64);
            var num1 = fx.Make(num, fx.Int(1));
            fx.Union(num1, fx.Make(num, fx.Int(2)));

            var dag = new TermDag();
            var variants = Extractor.ComputeCostsFromRootSorts(fx.Schema, [math]).ExtractVariants(num1, 2, dag);
            Assert.Equal(["(Num 1)", "(Num 2)"], variants.Select(v => dag.ToString(v.Term)).ToArray());

            fx.Subsume(num, fx.Int(2));
            variants = Extractor.ComputeCostsFromRootSorts(fx.Schema, [math]).ExtractVariants(num1, 2, dag);
            Assert.Equal(["(Num 1)"], variants.Select(v => dag.ToString(v.Term)).ToArray());
        }

        [Fact]
        public void RewriteSubsumedUnextractable()
        {
            // (rewrite (cheap) (expensive) :subsume) (cheap) (run 1): the run adds expensive, unions it with cheap and
            // subsumes cheap, so extracting cheap gives expensive.
            var fx = new ExtractionFixture();
            var math = new EqSort("Math");
            var expensive = fx.Constructor("expensive", math, 100);
            var cheap = fx.Constructor("cheap", math, 1);
            var cheapId = fx.Make(cheap);
            fx.Union(cheapId, fx.Make(expensive));
            fx.Subsume(cheap);

            Assert.Equal(("(expensive)", 100L), fx.Extract(math, fx.Canon(cheapId)));
        }

        [Fact]
        public void RewriteSubsumed()
        {
            // (rewrite (most-exp) (expensive) :subsume) then a cheap rewrite that cannot fire on the subsumed row.
            var fx = new ExtractionFixture();
            var math = new EqSort("Math");
            var expensive = fx.Constructor("expensive", math, 100);
            var mostExp = fx.Constructor("most-exp", math, 1000);
            var mostExpId = fx.Make(mostExp);
            fx.Union(mostExpId, fx.Make(expensive));
            fx.Subsume(mostExp);
            fx.Constructor("cheap", math, 1);

            Assert.Equal("(expensive)", fx.Extract(math, fx.Canon(mostExpId))?.Text);
        }

        [Fact]
        public void Subsume()
        {
            // (rewrite (Add a b) (Add b a)) with x = (Add (Num 1) (Num 2)) subsumed: one run commutes only y.
            var fx = new ExtractionFixture();
            var math = new EqSort("Math");
            var add = fx.Constructor("Add", math, null, math, math);
            var num = fx.Constructor("Num", math, null, fx.I64);
            var n1 = fx.Make(num, fx.Int(1));
            var n2 = fx.Make(num, fx.Int(2));
            var n3 = fx.Make(num, fx.Int(3));
            var n4 = fx.Make(num, fx.Int(4));
            var x = fx.Make(add, n1, n2);
            var y = fx.Make(add, n3, n4);
            fx.Subsume(add, n1, n2);
            fx.Union(y, fx.Make(add, n4, n3));

            var dag = new TermDag();
            Assert.Equal(2, Extractor.ComputeCostsFromRootSorts(fx.Schema, [math]).ExtractVariants(fx.Canon(y), 10, dag).Count);

            // (constructor otherConst () Math) (union x (otherConst)): then x's class has one extractable node.
            var otherConst = fx.Constructor("otherConst", math, null);
            fx.Union(x, fx.Make(otherConst));
            var variants = Extractor.ComputeCostsFromRootSorts(fx.Schema, [math]).ExtractVariants(fx.Canon(x), 10, dag);
            Assert.Equal(["(otherConst)"], variants.Select(v => dag.ToString(v.Term)).ToArray());
        }

        [Fact]
        public void SubsumedUnextractableRebuildArg()
        {
            // egglog src/lib.rs test_subsumed_unextractable_rebuild_arg: a subsumed row stays unextractable after a
            // rebuild changes one of its arguments' values.
            var fx = new ExtractionFixture();
            var math = new EqSort("Math");
            var container = fx.Constructor("container", math, null, math);
            var expensive = fx.Constructor("expensive", math, 100);
            var cheap = fx.Constructor("cheap", math, null);
            var cheap1 = fx.Constructor("cheap-1", math, null);
            var res = fx.Make(container, fx.Make(cheap));
            fx.Union(res, fx.Make(expensive));
            fx.Make(cheap);
            fx.Make(cheap1);
            fx.Subsume(container, fx.Get(cheap));

            var origCheap = fx.Get(cheap);
            var origCheap1 = fx.Get(cheap1);
            Assert.NotEqual(origCheap, origCheap1);

            fx.Union(fx.Get(cheap1), fx.Get(cheap));
            var newCheap = fx.Get(cheap);
            var newCheap1 = fx.Get(cheap1);
            Assert.Equal(newCheap, newCheap1);
            Assert.True(newCheap != origCheap || newCheap1 != origCheap1);

            Assert.Equal("(expensive)", fx.Extract(math, fx.Canon(res))?.Text);
        }

        [Fact]
        public void SubsumedUnextractableRebuildSelf()
        {
            // egglog src/lib.rs test_subsumed_unextractable_rebuild_self: a subsumed row stays unextractable after a
            // rebuild changes its own output.
            var fx = new ExtractionFixture();
            var math = new EqSort("Math");
            fx.Constructor("container", math, null, math);
            var expensive = fx.Constructor("expensive", math, 100);
            var cheap = fx.Constructor("cheap", math, null);
            fx.Make(expensive);
            var x = fx.Make(cheap);
            fx.Subsume(cheap);

            var origCheap = fx.Get(cheap);
            fx.Union(fx.Get(expensive), x);
            Assert.NotEqual(origCheap, fx.Get(cheap));

            Assert.Equal("(expensive)", fx.Extract(math, fx.Canon(x))?.Text);
        }

        [Fact]
        public void ExtractBestRequiresASingleRootSort()
        {
            var fx = new ExtractionFixture();
            var a = new EqSort("A");
            var b = new EqSort("B");
            var leafA = fx.Constructor("leafA", a, null);
            fx.Constructor("leafB", b, null);
            var value = fx.Make(leafA);

            var both = Extractor.ComputeCostsFromRootSorts(fx.Schema);
            Assert.Equal(new Sort[] { a, b }, both.RootSorts.ToArray());
            Assert.Throws<InvalidOperationException>(() => both.TryExtractBest(value, new TermDag(), out _));
            Assert.Throws<InvalidOperationException>(() => both.ExtractVariants(value, 1, new TermDag()));

            var one = Extractor.ComputeCostsFromRootSorts(fx.Schema, [a]);
            Assert.True(one.TryExtractBest(value, new TermDag(), out var result));
            Assert.Equal(1, result.Cost);
        }

        [Fact]
        public void ExtractValueToStringRendersTheTerm()
        {
            var fx = new ExtractionFixture();
            var op = new EqSort("Op");
            var add = fx.Constructor("Add", op, null, fx.I64, fx.I64);
            var expr = fx.Make(add, fx.Int(-7), fx.Int(1L << 40));
            Assert.Equal("(Add -7 1099511627776)", Extractor.ExtractValueToString(fx.Schema, op, expr, out var cost));
            Assert.Equal(3, cost);
        }

        [Fact]
        public void RootsOfBaseAndContainerSortsExtract()
        {
            // egglog's compute_cost_node: a base value costs its own cost; a container its elements' costs.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var leaf = fx.Constructor("leaf", e, 3);
            var vec = new TestVecSort("VecE", e);
            var leafId = fx.Make(leaf);
            var holder = fx.Function("holder", vec);
            fx.Set(holder, [], fx.EGraph.GetContainerValue(new VecContainer(leafId.Rep, leafId.Rep)));

            Assert.Equal(("7", 1L), fx.Extract(fx.I64, fx.Int(7)));
            Assert.Equal(("\"hi\"", 1L), fx.Extract(fx.String, fx.Str("hi")));
            Assert.Equal(("(vec-of (leaf) (leaf))", 6L), fx.Extract(vec, fx.Get(holder)));
        }

    }

}
