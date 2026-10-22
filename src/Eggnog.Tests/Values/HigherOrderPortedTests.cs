using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// egglog's higher-order container primitives over function values, <c>unstable-vec-map</c> (src/sort/vec.rs) and
    /// <c>unstable-multiset-map</c>, <c>-flat-map</c> and <c>-reduce</c> (src/sort/multiset.rs) at 5e655cae, ported from
    /// egglog's tests: <c>tests/vec.egg</c>, <c>tests/web-demo/multiset.egg</c>,
    /// <c>tests/typed_primitive_unstable_app.egg</c> and <c>tests/factoring-multisets.egg</c>.
    /// </summary>
    public sealed class HigherOrderPortedTests
    {

        /// <summary>
        /// vec.egg, "Test unstable-vec-map": a constructor mapped in a write makes its terms; a function mapped drops the
        /// elements it has no row for, in a write and in a check.
        /// </summary>
        [Fact]
        public void AVectorMapsAndSkipsWhereItsFunctionIsUndefined()
        {
            // (datatype X (a) (b)) (sort Y) (sort VY (Vec Y)) (sort XY (UnstableFn (X) Y)) (constructor y (X) Y)
            var graph = new EGraph();
            graph.DefineSort<Node>("X");
            var a = graph.DefineConstructor<Node>("a");
            var b = graph.DefineConstructor<Node>("b");
            graph.DefineSort<NodeList>("Y");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<NodeList>();
            graph.Values.RegisterFn<Node, NodeList>();
            var y = graph.DefineConstructor<Node, NodeList>("y");
            var map = Primitives.UnstableVecMap<Node, NodeList>();
            var xs = Primitives.VecOf<Node>();
            var ys = Primitives.VecOf<NodeList>();
            var root = graph.Root;

            // (let $mapped (unstable-vec-map (unstable-fn "y") (vec-of (a) (b)))) (check (= $mapped (vec-of (y (a)) (y (b)))))
            var mapped = graph.DefineFunction<Vec<NodeList>>("mapped");
            root.Assume(mapped.Of().Is(map.Of(Primitives.UnstableFn<Node, NodeList>(y).Of(), xs.Of(a.Of(), b.Of()))));
            Assert.True(root.Check(mapped.Of().Is(ys.Of(y.Of(a.Of()), y.Of(b.Of())))));

            // (function xToY (X) Y :no-merge) (set (xToY (a)) (y (a)))
            // (let $mapped (unstable-vec-map (unstable-fn "xToY") (vec-of (a)))) (check (= $mapped (vec-of (y (a)))))
            var xToY = graph.DefineFunction<Node, NodeList>("xToY");
            root.Assume(xToY.Of(a.Of()).Is(y.Of(a.Of())));
            var mappedA = graph.DefineFunction<Vec<NodeList>>("mapped-a");
            var viaFunction = Primitives.UnstableFn<Node, NodeList>(xToY);
            root.Assume(mappedA.Of().Is(map.Of(viaFunction.Of(), xs.Of(a.Of()))));
            Assert.True(root.Check(mappedA.Of().Is(ys.Of(y.Of(a.Of())))));

            // (check (= (unstable-vec-map (unstable-fn "xToY") (vec-of (a) (b))) (vec-of (y (a)))))
            Assert.True(root.Check(map.Of(viaFunction.Of(), xs.Of(a.Of(), b.Of())).Is(ys.Of(y.Of(a.Of())))));
        }

        /// <summary>
        /// web-demo/multiset.egg, "map" and "map to other type": a constructor mapped over a multiset makes its terms,
        /// which a rewrite then merges; and into a multiset of another sort.
        /// </summary>
        [Fact]
        public void AMultiSetMapsToItsSortAndAnother()
        {
            // (datatype Math (Num i64)) (sort Maths (MultiSet Math)) (let $xs (multiset-of (Num 1) (Num 2) (Num 3)))
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterMultiSet<Node>();
            var of = Primitives.MultiSetOf<Node>();
            var xs = graph.DefineFunction<MultiSet<Node>>("xs");
            var root = graph.Root;
            root.Assume(xs.Of().Is(of.Of(num.Of(1L), num.Of(2L), num.Of(3L))));

            // (constructor square (Math) Math) (rewrite (square (Num x)) (Num (* x x))) (sort MathToMath (UnstableFn (Math) Math))
            var square = graph.DefineConstructor<Node, Node>("square");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("square", r =>
            {
                var x = r.Var<long>("x");
                r.Rewrite(square.Of(num.Of(x)), num.Of(Primitives.Multiply.Of(x, x)));
            });
            graph.Values.RegisterFn<Node, Node>();

            // (let $squared-xs (unstable-multiset-map (unstable-fn "square") $xs)) (run 1)
            // (check (= (multiset-of (Num 1) (Num 4) (Num 9)) $squared-xs))
            var squared = graph.DefineFunction<MultiSet<Node>>("squared-xs");
            root.Assume(squared.Of().Is(Primitives.UnstableMultiSetMap<Node, Node>().Of(Primitives.UnstableFn<Node, Node>(square).Of(), xs.Of())));
            root.Run(rules);
            Assert.True(root.Check(of.Of(num.Of(1L), num.Of(4L), num.Of(9L)).Is(squared.Of())));

            // (datatype Math2 (FromMath Math)) (sort MathToMath2 (UnstableFn (Math) Math2)) (sort Maths2Multiset (MultiSet Math2))
            // (let $mapped-xs (unstable-multiset-map (unstable-fn "FromMath") $xs)) (run 1)
            // (check (= (multiset-of (FromMath (Num 1)) (FromMath (Num 2)) (FromMath (Num 3))) $mapped-xs))
            graph.DefineSort<NodeList>("Math2");
            var fromMath = graph.DefineConstructor<Node, NodeList>("FromMath");
            graph.Values.RegisterFn<Node, NodeList>();
            graph.Values.RegisterMultiSet<NodeList>();
            var mappedXs = graph.DefineFunction<MultiSet<NodeList>>("mapped-xs");
            root.Assume(mappedXs.Of().Is(Primitives.UnstableMultiSetMap<Node, NodeList>().Of(Primitives.UnstableFn<Node, NodeList>(fromMath).Of(), xs.Of())));
            root.Run(rules);
            Assert.True(root.Check(Primitives.MultiSetOf<NodeList>().Of(fromMath.Of(num.Of(1L)), fromMath.Of(num.Of(2L)), fromMath.Of(num.Of(3L))).Is(mappedXs.Of())));
        }

        /// <summary>
        /// web-demo/multiset.egg, "flat-map": an element the function has a row for becomes its multiset, counted as the
        /// element is; one it has none for stays.
        /// </summary>
        [Fact]
        public void AMultiSetFlatMapsAndKeepsWhereItsFunctionIsUndefined()
        {
            // (Num 2), which egglog's $xs made before; (function to-multiset (Math) Maths :no-merge)
            // (set (to-multiset (Num 1)) (multiset-of (Num 1) (Num 1)))
            // (sort MathsToMaths (UnstableFn (Math) Maths))
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterMultiSet<Node>();
            var of = Primitives.MultiSetOf<Node>();
            var toMultiSet = graph.DefineFunction<Node, MultiSet<Node>>("to-multiset");
            var root = graph.Root;
            root.Make(num.Of(2L));
            root.Assume(toMultiSet.Of(num.Of(1L)).Is(of.Of(num.Of(1L), num.Of(1L))));
            graph.Values.RegisterFn<Node, MultiSet<Node>>();

            // (check (= (unstable-multiset-flat-map (unstable-fn "to-multiset") (multiset-of (Num 1) (Num 2)))
            //           (multiset-of (Num 1) (Num 1) (Num 2))))
            var flatMap = Primitives.UnstableMultiSetFlatMap<Node>();
            Assert.True(root.Check(flatMap.Of(Primitives.UnstableFn<Node, MultiSet<Node>>(toMultiSet).Of(), of.Of(num.Of(1L), num.Of(2L)))
                .Is(of.Of(num.Of(1L), num.Of(1L), num.Of(2L)))));

            // The same in a write, whose results count as their elements: (multiset-of (Num 1) (Num 1) (Num 2)) flattens
            // to four of (Num 1).
            var flat = graph.DefineFunction<MultiSet<Node>>("flat");
            root.Assume(flat.Of().Is(flatMap.Of(Primitives.UnstableFn<Node, MultiSet<Node>>(toMultiSet).Of(), of.Of(num.Of(1L), num.Of(1L), num.Of(2L)))));
            Assert.Equal(4L, root.Get(Primitives.MultiSetCount<Node>().Of(flat.Of(), num.Of(1L))));
            Assert.Equal(1L, root.Get(Primitives.MultiSetCount<Node>().Of(flat.Of(), num.Of(2L))));
        }

        /// <summary>
        /// web-demo/multiset.egg, "fold": a constructor reduced over a multiset in a write, in egglog's order, and the
        /// initial value of an empty one; in a check, a read, the constructor makes no term, so the reduction fails.
        /// </summary>
        [Fact]
        public void AMultiSetReducesInCellOrder()
        {
            // (datatype Math (Num i64)) (let $xs (multiset-of (Num 1) (Num 2) (Num 3)))
            // (constructor add (Math Math) Math) (sort MathAddFn (UnstableFn (Math Math) Math))
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterMultiSet<Node>();
            var of = Primitives.MultiSetOf<Node>();
            var xs = graph.DefineFunction<MultiSet<Node>>("xs");
            var root = graph.Root;
            root.Assume(xs.Of().Is(of.Of(num.Of(1L), num.Of(2L), num.Of(3L))));
            var add = graph.DefineConstructor<Node, Node, Node>("add");
            graph.Values.RegisterFn<Node, Node, Node>();
            var reduce = Primitives.UnstableMultiSetReduce<Node>();
            var addFn = Primitives.UnstableFn<Node, Node, Node>(add);

            // (let $sum-xs (unstable-multiset-reduce (unstable-fn "add") (Num 0) $xs)) (check (= $sum-xs (add (add (Num 1) (Num 2)) (Num 3))))
            var sum = graph.DefineFunction<Node>("sum-xs");
            root.Assume(sum.Of().Is(reduce.Of(addFn.Of(), num.Of(0L), xs.Of())));
            Assert.True(root.AreEqual(root.Get(sum.Of()), add.Of(add.Of(num.Of(1L), num.Of(2L)), num.Of(3L))));

            // (let $sum-empty (unstable-multiset-reduce (unstable-fn "add") (Num 0) (multiset-of))) (check (= $sum-empty (Num 0)))
            var empty = graph.DefineFunction<Node>("sum-empty");
            root.Assume(empty.Of().Is(reduce.Of(addFn.Of(), num.Of(0L), of.Of())));
            Assert.True(root.AreEqual(root.Get(empty.Of()), num.Of(0L)));

            // In a check, a read, the constructor is not valid.
            var refused = Assert.ThrowsAny<Exception>(() => root.Check(reduce.Of(addFn.Of(), num.Of(0L), xs.Of()).Is(sum.Of())));
            Assert.Contains("unstable-fn over 'add'", refused.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// typed_primitive_unstable_app.egg, part 1b: a function value of the higher-order primitive itself, applied to a
        /// function value and a vector.
        /// </summary>
        [Fact]
        public void AFunctionValueAppliesAHigherOrderPrimitive()
        {
            // (sort BoolVec (Vec bool)) (sort BoolToBool (UnstableFn (bool) bool)) (sort VecBoolMap (UnstableFn (BoolToBool BoolVec) BoolVec))
            // (check (= (unstable-app (unstable-fn "unstable-vec-map") (unstable-fn "not") (vec-of true false)) (vec-of false true)))
            var graph = new EGraph();
            graph.Values.RegisterVec<bool>();
            graph.Values.RegisterFn<bool, bool>();
            graph.Values.RegisterFn<UnstableFn<bool, bool>, Vec<bool>, Vec<bool>>();
            var vecMap = Primitives.UnstableFn<UnstableFn<bool, bool>, Vec<bool>, Vec<bool>>(Primitives.UnstableVecMap<bool, bool>());
            var not = Primitives.UnstableFn<bool, bool>(BooleanPrimitives.Not);
            var app = Primitives.UnstableApp<UnstableFn<bool, bool>, Vec<bool>, Vec<bool>>();
            var bools = Primitives.VecOf<bool>();
            Assert.True(graph.Root.Check(app.Of(vecMap.Of(), not.Of(), bools.Of(true, false)).Is(bools.Of(false, true))));
        }

        /// <summary>
        /// factoring-multisets.egg's to_polynomial and from_polynomial rulesets over a smaller expression: naive rules
        /// whose bodies map a flat-map of a function's rows over a multiset of multisets of classes, and a head that
        /// reduces a constructor over a map of reductions of a constructor. egglog at 5e655cae, the same program with the
        /// sorts it declares: after to_polynomial, $x is (polynomial (multiset-of (multiset-of d) (multiset-of e)
        /// (multiset-of a b c))) and polynomial holds 4 rows; after from_polynomial, $x extracts as
        /// (Value___add__ (Value___add__ d e) (Value___mul__ (Value___mul__ a b) c)) and polynomial holds none.
        /// </summary>
        [Fact]
        public void APolynomialFlattensAndFoldsBack()
        {
            // (sort Value) (constructor Value___mul__ (Value Value) Value) (constructor Value___add__ (Value Value) Value)
            // (constructor Value_var (String) Value) (sort Int) (constructor Value_from_int (Int) Value) (constructor Int___init__ (i64) Int)
            var graph = new EGraph();
            graph.DefineSort<Node>("Value");
            var mul = graph.DefineConstructor<Node, Node, Node>("Value___mul__");
            var add = graph.DefineConstructor<Node, Node, Node>("Value___add__");
            var var = graph.DefineConstructor<string, Node>("Value_var");
            graph.DefineSort<NodeList>("Int");
            var fromInt = graph.DefineConstructor<NodeList, Node>("Value_from_int");
            var intInit = graph.DefineConstructor<long, NodeList>("Int___init__");
            var root = graph.Root;

            // (let $x (Value___add__ (Value___mul__ (Value___mul__ a b) c) (Value___add__ d e)))
            var x = root.Make(add.Of(mul.Of(mul.Of(var.Of("a"), var.Of("b")), var.Of("c")), add.Of(var.Of("d"), var.Of("e"))));

            // (sort MultiSet[Value] (MultiSet Value)) (sort MultiSet[MultiSet[Value]] (MultiSet MultiSet[Value]))
            // (constructor polynomial (MultiSet[MultiSet[Value]]) Value)
            // (function get_sole_polynomial (MultiSet[Value]) MultiSet[MultiSet[Value]] :merge new)
            // (function get_monomial (Value) MultiSet[Value] :merge new)
            graph.Values.RegisterMultiSet<Node>();
            graph.Values.RegisterMultiSet<MultiSet<Node>>();
            var polynomial = graph.DefineConstructor<MultiSet<MultiSet<Node>>, Node>("polynomial");
            var getSole = graph.DefineFunction<MultiSet<Node>, MultiSet<MultiSet<Node>>>("get_sole_polynomial", FunctionMerge.New<MultiSet<MultiSet<Node>>>());
            var getMonomial = graph.DefineFunction<Node, MultiSet<Node>>("get_monomial", FunctionMerge.New<MultiSet<Node>>());
            var ms = Primitives.MultiSetOf<Node>();
            var mss = Primitives.MultiSetOf<MultiSet<Node>>();
            graph.Values.RegisterFn<MultiSet<Node>, MultiSet<Node>>();
            graph.Values.RegisterFn<Node, MultiSet<Node>>();
            graph.Values.RegisterFn<MultiSet<Node>, MultiSet<MultiSet<Node>>>();
            graph.Values.RegisterFn<Node, Node, Node>();
            graph.Values.RegisterFn<MultiSet<Node>, Node>();

            var to = graph.DefineRuleset("to");
            // (rule ((= _n3 (Value___add__ _n1 _n2)) (= _mss (multiset-of (multiset-of _n1) (multiset-of _n2))))
            //       ((union _n3 (polynomial _mss)) (set (get_sole_polynomial (multiset-of (polynomial _mss))) _mss) (delete (Value___add__ _n1 _n2))))
            to.DefineRule("add", r =>
            {
                var (n1, n2, n3) = r.Vars<Node, Node, Node>("_n1", "_n2", "_n3");
                var m = r.Var<MultiSet<MultiSet<Node>>>("_mss");
                r.When(n3.Is(add.Of(n1, n2)), m.Is(mss.Of(ms.Of(n1), ms.Of(n2))));
                r.Then(n3.Is(polynomial.Of(m)), getSole.Of(ms.Of(polynomial.Of(m))).Is(m));
                r.Delete(add.Of(n1, n2));
            });

            // (rule ((= _n3 (Value___mul__ _n1 _n2)) (= _ms (multiset-of _n1 _n2)))
            //       ((union _n3 (polynomial (multiset-of _ms))) (set (get_monomial (polynomial (multiset-of _ms))) _ms) (delete (Value___mul__ _n1 _n2))))
            to.DefineRule("mul", r =>
            {
                var (n1, n2, n3) = r.Vars<Node, Node, Node>("_n1", "_n2", "_n3");
                var m = r.Var<MultiSet<Node>>("_ms");
                r.When(n3.Is(mul.Of(n1, n2)), m.Is(ms.Of(n1, n2)));
                r.Then(n3.Is(polynomial.Of(mss.Of(m))), getMonomial.Of(polynomial.Of(mss.Of(m))).Is(m));
                r.Delete(mul.Of(n1, n2));
            });

            // (rule ((= _n1 (polynomial _mss))
            //        (= _mss1 (unstable-multiset-map (unstable-fn "unstable-multiset-flat-map" (unstable-fn "get_monomial")) _mss))
            //        (!= _mss _mss1))
            //       ((union _n1 (polynomial _mss1)) (delete (polynomial _mss)) (set (get_sole_polynomial (multiset-of _n1)) _mss1)) :naive)
            var flatMonomials = Primitives.UnstableFn<MultiSet<Node>, MultiSet<Node>>(Primitives.UnstableMultiSetFlatMap<Node>())
                .Of(Primitives.UnstableFn<Node, MultiSet<Node>>(getMonomial).Of());
            to.DefineRule("unwrap monomial", r =>
            {
                var n1 = r.Var<Node>("_n1");
                var (m, m1) = r.Vars<MultiSet<MultiSet<Node>>, MultiSet<MultiSet<Node>>>("_mss", "_mss1");
                r.Naive();
                r.When(n1.Is(polynomial.Of(m)), m1.Is(Primitives.UnstableMultiSetMap<MultiSet<Node>, MultiSet<Node>>().Of(flatMonomials, m)), m.IsNot(m1));
                r.Then(n1.Is(polynomial.Of(m1)), getSole.Of(ms.Of(n1)).Is(m1));
                r.Delete(polynomial.Of(m));
            });

            // (rule ((= _n1 (polynomial _mss)) (= _mss1 (unstable-multiset-flat-map (unstable-fn "get_sole_polynomial") _mss)) (!= _mss _mss1))
            //       ((union _n1 (polynomial _mss1)) (delete (polynomial _mss)) (set (get_sole_polynomial (multiset-of _n1)) _mss1)) :naive)
            to.DefineRule("unwrap polynomial", r =>
            {
                var n1 = r.Var<Node>("_n1");
                var (m, m1) = r.Vars<MultiSet<MultiSet<Node>>, MultiSet<MultiSet<Node>>>("_mss", "_mss1");
                r.Naive();
                r.When(n1.Is(polynomial.Of(m)), m1.Is(Primitives.UnstableMultiSetFlatMap<MultiSet<Node>>().Of(Primitives.UnstableFn<MultiSet<Node>, MultiSet<MultiSet<Node>>>(getSole).Of(), m)), m.IsNot(m1));
                r.Then(n1.Is(polynomial.Of(m1)), getSole.Of(ms.Of(n1)).Is(m1));
                r.Delete(polynomial.Of(m));
            });

            // (rule ((= _n (polynomial _mss)))
            //       ((union _n (unstable-multiset-reduce (unstable-fn "Value___add__") (Value_from_int (Int___init__ 0))
            //           (unstable-multiset-map (unstable-fn "unstable-multiset-reduce" (unstable-fn "Value___mul__") (Value_from_int (Int___init__ 1))) _mss)))
            //        (delete (polynomial _mss))))
            var from = graph.DefineRuleset("from");
            from.DefineRule("from", r =>
            {
                var n = r.Var<Node>("_n");
                var m = r.Var<MultiSet<MultiSet<Node>>>("_mss");
                var product = Primitives.UnstableFn<MultiSet<Node>, Node>(Primitives.UnstableMultiSetReduce<Node>())
                    .Of(Primitives.UnstableFn<Node, Node, Node>(mul).Of(), fromInt.Of(intInit.Of(1L)));
                r.When(n.Is(polynomial.Of(m)));
                r.Then(n.Is(Primitives.UnstableMultiSetReduce<Node>().Of(Primitives.UnstableFn<Node, Node, Node>(add).Of(), fromInt.Of(intInit.Of(0L)),
                    Primitives.UnstableMultiSetMap<MultiSet<Node>, Node>().Of(product, m))));
                r.Delete(polynomial.Of(m));
            });

            // (run-schedule (saturate (run to)))
            root.Run(Schedule.Saturate(to));
            Assert.Equal(4, root.CountRows(polynomial));
            Assert.True(root.AreEqual(x, polynomial.Of(mss.Of(ms.Of(var.Of("d")), ms.Of(var.Of("e")), ms.Of(var.Of("a"), var.Of("b"), var.Of("c"))))));

            // (run-schedule (saturate (run from))) (extract $x)
            root.Run(Schedule.Saturate(from));
            Assert.Equal(0, root.CountRows(polynomial));
            Assert.Equal(
                "(Value___add__ (Value___add__ (Value_var \"d\") (Value_var \"e\")) (Value___mul__ (Value___mul__ (Value_var \"a\") (Value_var \"b\")) (Value_var \"c\")))",
                root.Extract(x).ToString());
        }

        /// <summary>
        /// factoring-multisets.egg: a function value of a higher-order primitive partially applied to a function value,
        /// <c>(unstable-multiset-map (unstable-fn "unstable-multiset-flat-map" (unstable-fn "get_monomial")) _mss)</c>,
        /// and of a reduction partially applied to its function and initial value, mapped over a multiset of multisets.
        /// </summary>
        [Fact]
        public void HigherOrderPrimitivesNest()
        {
            var graph = new EGraph();
            graph.Values.RegisterMultiSet<long>();
            graph.Values.RegisterMultiSet<MultiSet<long>>();
            graph.Values.RegisterFn<long, MultiSet<long>>();
            graph.Values.RegisterFn<MultiSet<long>, MultiSet<long>>();
            var twice = Primitive.Create<MultiSet<long>>("twice", [typeof(long)], args => MultiSet.Of((long)args[0], (long)args[0]));
            var flatTwice = Primitives.UnstableFn<MultiSet<long>, MultiSet<long>>(Primitives.UnstableMultiSetFlatMap<long>())
                .Of(Primitives.UnstableFn<long, MultiSet<long>>(twice).Of());
            var mss = MultiSet.Of(MultiSet.Of(1L, 2L), MultiSet.Of(3L));
            var map = Primitives.UnstableMultiSetMap<MultiSet<long>, MultiSet<long>>();
            Assert.True(graph.Root.Check(map.Of(flatTwice, mss).Is(MultiSet.Of(MultiSet.Of(1L, 1L, 2L, 2L), MultiSet.Of(3L, 3L)))));

            // (unstable-multiset-map (unstable-fn "unstable-multiset-reduce" (unstable-fn "*") 1) _mss): each multiset's
            // product.
            graph.Values.RegisterFn<long, long, long>();
            graph.Values.RegisterFn<MultiSet<long>, long>();
            var product = Primitives.UnstableFn<MultiSet<long>, long>(Primitives.UnstableMultiSetReduce<long>())
                .Of(Primitives.UnstableFn<long, long, long>(Primitives.Multiply).Of(), 1L);
            var products = graph.DefineFunction<MultiSet<long>>("products");
            graph.Root.Assume(products.Of().Is(Primitives.UnstableMultiSetMap<MultiSet<long>, long>().Of(product, MultiSet.Of(MultiSet.Of(2L, 3L), MultiSet.Of(4L), MultiSet<long>.Empty))));
            Assert.Equal(MultiSet.Of(6L, 4L, 1L), graph.Root.Get(products.Of()));
        }

    }

}
