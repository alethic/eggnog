using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Function values, egglog's <c>UnstableFn</c> sort and its <c>unstable-fn</c> and <c>unstable-app</c> primitives
    /// (src/sort/fn.rs at 5e655cae), ported from egglog's tests: <c>tests/typed_primitive_unstable_app.egg</c>, the
    /// dispatch matrix of <c>tests/typed_primitive.rs</c>, <c>tests/naive_unstable_fn_via_global.rs</c>,
    /// <c>tests/web-demo/unstable-fn.egg</c> and <c>tests/repro-738-fn-sort.egg</c>.
    /// </summary>
    public sealed class UnstableFnPortedTests
    {

        static readonly Type[] Long = [typeof(long)];

        /// <summary>
        /// typed_primitive_unstable_app.egg, part 1: a pure primitive applied in a check, in a merge, and partially
        /// applied.
        /// </summary>
        [Fact]
        public void APurePrimitiveAppliesEverywhere()
        {
            var graph = new EGraph();

            // (sort IntFn (UnstableFn (i64 i64) i64)) (let plus (unstable-fn "+")) (check (= (unstable-app plus 2 3) 5))
            graph.Values.RegisterFn<long, long, long>();
            var plus = graph.DefineFunction<UnstableFn<long, long, long>>("plus");
            graph.Root.Assume(plus.Of().Is(Primitives.UnstableFn<long, long, long>(Primitives.Add).Of()));
            Assert.True(graph.Root.Check(Primitives.UnstableApp<long, long, long>().Of(plus.Of(), 2L, 3L).Is(5L)));
            Assert.Equal(UnstableFn.Of<long, long, long>(Primitives.Add), graph.Root.Get(plus.Of()));

            // (sort I2I (UnstableFn (i64) i64))
            // (function merge-plus () i64 :merge (unstable-app (unstable-fn "+" old) new)) (set (merge-plus) 1) (set (merge-plus) 2)
            graph.Values.RegisterFn<long, long>();
            var app = Primitives.UnstableApp<long, long>();
            var add = Primitives.UnstableFn<long, long>(Primitives.Add);
            var mergePlus = graph.DefineFunction("merge-plus", FunctionMerge.Expression<long>((old, @new) => app.Of(add.Of(old), @new)));
            graph.Root.Assume(mergePlus.Of().Is(1L));
            graph.Root.Assume(mergePlus.Of().Is(2L));
            Assert.True(graph.Root.Check(mergePlus.Of().Is(3L)));

            // (check (= (unstable-app (unstable-fn "+" 1) 2) 3)) (check (= (unstable-app (unstable-fn "+" 1.0) 2.0) 3.0))
            Assert.True(graph.Root.Check(app.Of(add.Of(1L), 2L).Is(3L)));
            graph.Values.RegisterFn<double, double>();
            Assert.True(graph.Root.Check(Primitives.UnstableApp<double, double>().Of(Primitives.UnstableFn<double, double>(DoublePrimitives.Add).Of(1.0), 2.0).Is(3.0)));

            // (check (= (unstable-app (unstable-fn "not") true) false)) (check (= (unstable-app (unstable-fn "vec-empty")) (vec-empty)))
            graph.Values.RegisterFn<bool, bool>();
            Assert.True(graph.Root.Check(Primitives.UnstableApp<bool, bool>().Of(Primitives.UnstableFn<bool, bool>(BooleanPrimitives.Not).Of(), true).Is(false)));
            graph.Values.RegisterVec<long>();
            graph.Values.RegisterFn<Vec<long>>();
            Assert.True(graph.Root.Check(Primitives.UnstableApp<Vec<long>>().Of(Primitives.UnstableFn<Vec<long>>(Primitives.VecEmpty<long>()).Of()).Is(Primitives.VecEmpty<long>().Of())));
        }

        /// <summary>
        /// typed_primitive_unstable_app.egg, parts 2 to 5: a constructor applied where the context writes makes its
        /// term, and fails where it reads; a function applied where the context reads reads its row, and fails where it
        /// does not.
        /// </summary>
        [Fact]
        public void ATableAppliesWhereItsContextAllows()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterFn<long, Node>();
            var mathApp = Primitives.UnstableApp<long, Node>();

            // (let make (unstable-fn "Num")) (fail (check (Num 99)))
            var make = graph.DefineFunction<UnstableFn<long, Node>>("make");
            graph.Root.Assume(make.Of().Is(Primitives.UnstableFn<long, Node>(num).Of()));
            Assert.False(graph.Root.Check(Fact.Exists(num.Of(99L))));

            // (relation Trigger ()) (Trigger) (rule ((Trigger)) ((let $minted (unstable-app make 99)))) (run 1) (check (Num 99))
            var trigger = graph.DefineRelation("Trigger");
            var minted = graph.DefineFunction<long, Node>("minted");
            graph.Root.Assume(trigger.Of());
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("mint", r =>
            {
                var f = r.Var<UnstableFn<long, Node>>("f");
                r.When(trigger.Of(), f.Is(make.Of()));
                r.Then(minted.Of(0L).Is(mathApp.Of(f, 99L)));
            });
            graph.Root.Run(rules);
            Assert.True(graph.Root.Check(Fact.Exists(num.Of(99L))));

            // (fail (check (= (unstable-app make 42) (Num 42)))) (fail (check (Num 42))): a check reads, and mints nothing.
            var refused = Assert.ThrowsAny<Exception>(() => graph.Root.Check(mathApp.Of(make.Of(), 42L).Is(num.Of(42L))));
            Assert.Contains("unstable-fn over 'Num'", refused.ToString(), StringComparison.Ordinal);
            Assert.False(graph.Root.Check(Fact.Exists(num.Of(42L))));

            // (function dbl (i64) i64 :no-merge) (set (dbl 7) 14) (let twice (unstable-fn "dbl"))
            // (rule ((Trigger)) ((set (dbl-out 7) (unstable-app twice 7))) :naive) (run 1) (check (= (dbl-out 7) 14))
            var dbl = graph.DefineFunction<long, long>("dbl");
            var dblOut = graph.DefineFunction<long, long>("dbl-out");
            graph.Values.RegisterFn<long, long>();
            var app = Primitives.UnstableApp<long, long>();
            graph.Root.Assume(dbl.Of(7L).Is(14L));
            var twice = graph.DefineFunction<UnstableFn<long, long>>("twice");
            graph.Root.Assume(twice.Of().Is(Primitives.UnstableFn<long, long>(dbl).Of()));
            rules.DefineRule("double", r =>
            {
                var f = r.Var<UnstableFn<long, long>>("f");
                r.Naive();
                r.When(trigger.Of(), f.Is(twice.Of()));
                r.Then(dblOut.Of(7L).Is(app.Of(f, 7L)));
            });
            graph.Root.Run(rules);
            Assert.True(graph.Root.Check(dblOut.Of(7L).Is(14L)));

            // (check (= (unstable-app twice 7) 14)): a check reads.
            Assert.True(graph.Root.Check(app.Of(twice.Of(), 7L).Is(14L)));
        }

        /// <summary>
        /// typed_primitive.rs, <c>unstable_app_dispatch_matrix</c>: an echo primitive of each access, applied through a
        /// function value in each context; it runs where its access is allowed (decisions row 12), and fails elsewhere.
        /// </summary>
        [Theory]
        [InlineData(PrimitiveAccess.Pure, PrimitiveAccess.Pure, true)]
        [InlineData(PrimitiveAccess.Pure, PrimitiveAccess.Read, true)]
        [InlineData(PrimitiveAccess.Pure, PrimitiveAccess.Write, true)]
        [InlineData(PrimitiveAccess.Pure, PrimitiveAccess.Full, true)]
        [InlineData(PrimitiveAccess.Read, PrimitiveAccess.Pure, false)]
        [InlineData(PrimitiveAccess.Read, PrimitiveAccess.Read, true)]
        [InlineData(PrimitiveAccess.Read, PrimitiveAccess.Write, false)]
        [InlineData(PrimitiveAccess.Read, PrimitiveAccess.Full, true)]
        [InlineData(PrimitiveAccess.Write, PrimitiveAccess.Pure, false)]
        [InlineData(PrimitiveAccess.Write, PrimitiveAccess.Read, false)]
        [InlineData(PrimitiveAccess.Write, PrimitiveAccess.Write, true)]
        [InlineData(PrimitiveAccess.Write, PrimitiveAccess.Full, true)]
        [InlineData(PrimitiveAccess.Full, PrimitiveAccess.Pure, false)]
        [InlineData(PrimitiveAccess.Full, PrimitiveAccess.Read, false)]
        [InlineData(PrimitiveAccess.Full, PrimitiveAccess.Write, false)]
        [InlineData(PrimitiveAccess.Full, PrimitiveAccess.Full, true)]
        public void AnApplicationDispatchesOnItsContext(PrimitiveAccess access, PrimitiveAccess context, bool succeeds)
        {
            var graph = new EGraph();
            graph.Values.RegisterFn<long, long>();
            var echo = access switch
            {
                PrimitiveAccess.Pure => (Primitive)Primitive.Create<long>("p", Long, args => (long)args[0]),
                PrimitiveAccess.Read => Primitive.CreateRead("p", Long, (ITableReader _, ReadOnlySpan<object> args, out long result) => { result = (long)args[0]; return true; }),
                PrimitiveAccess.Write => Primitive.CreateWrite("p", Long, (ITableWriter _, ReadOnlySpan<object> args, out long result) => { result = (long)args[0]; return true; }),
                _ => Primitive.CreateFull("p", Long, (ITables _, ReadOnlySpan<object> args, out long result) => { result = (long)args[0]; return true; }),
            };

            // (sort Fn (UnstableFn (i64) i64)) (let $f (unstable-fn "p")) (function out (i64) i64 :no-merge)
            var f = graph.DefineFunction<UnstableFn<long, long>>("f");
            var output = graph.DefineFunction<long, long>("out");
            var app = Primitives.UnstableApp<long, long>();
            graph.Root.Assume(f.Of().Is(Primitives.UnstableFn<long, long>(echo).Of()));
            var rules = graph.DefineRuleset("rules");

            void Apply()
            {
                switch (context)
                {
                    case PrimitiveAccess.Pure:
                        // (rule ((= y (unstable-app $f 7))) ((set (out 0) y))) (run 1) (check (= (out 0) 7))
                        rules.DefineRule("body", r =>
                        {
                            var (fn, y) = r.Vars<UnstableFn<long, long>, long>("fn", "y");
                            r.When(fn.Is(f.Of()), y.Is(app.Of(fn, 7L)));
                            r.Then(output.Of(0L).Is(y));
                        });
                        graph.Root.Run(rules);
                        Assert.True(graph.Root.Check(output.Of(0L).Is(7L)));
                        break;
                    case PrimitiveAccess.Read:
                        // (check (= (unstable-app $f 7) 7))
                        Assert.True(graph.Root.Check(app.Of(f.Of(), 7L).Is(7L)));
                        break;
                    case PrimitiveAccess.Write:
                        // (rule () ((set (out 0) (unstable-app $f 7)))) (run 1) (check (= (out 0) 7))
                        rules.DefineRule("head", r =>
                        {
                            var fn = r.Var<UnstableFn<long, long>>("fn");
                            r.When(fn.Is(f.Of()));
                            r.Then(output.Of(0L).Is(app.Of(fn, 7L)));
                        });
                        graph.Root.Run(rules);
                        Assert.True(graph.Root.Check(output.Of(0L).Is(7L)));
                        break;
                    default:
                        // (let $r (unstable-app $f 7)) (check (= $r 7))
                        graph.Root.Assume(output.Of(0L).Is(app.Of(f.Of(), 7L)));
                        Assert.True(graph.Root.Check(output.Of(0L).Is(7L)));
                        break;
                }
            }

            if (succeeds)
                Apply();
            else
                Assert.Contains("unstable-fn over 'p'", Assert.ThrowsAny<Exception>(Apply).ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// naive_unstable_fn_via_global.rs: a function's application in a rule's body or head reads the tables, which a
        /// semi-naive rule does not track, so it fails there with a message that names the function and the naive rule;
        /// a naive rule reads it.
        /// </summary>
        [Fact]
        public void AFunctionsApplicationInARuleNeedsANaiveRule()
        {
            // (function dbl (i64) i64 :no-merge) (set (dbl 7) 14) (sort I2I (UnstableFn (i64) i64)) (let $f (unstable-fn "dbl"))
            static (EGraph Graph, Function<UnstableFn<long, long>> F, Function<long, long> Out) Program()
            {
                var graph = new EGraph();
                var dbl = graph.DefineFunction<long, long>("dbl");
                graph.Root.Assume(dbl.Of(7L).Is(14L));
                graph.Values.RegisterFn<long, long>();
                var f = graph.DefineFunction<UnstableFn<long, long>>("f");
                graph.Root.Assume(f.Of().Is(Primitives.UnstableFn<long, long>(dbl).Of()));
                return (graph, f, graph.DefineFunction<long, long>("out"));
            }

            var app = Primitives.UnstableApp<long, long>();
            foreach (var naive in new[] { true, false })
            {
                foreach (var inBody in new[] { true, false })
                {
                    var (graph, f, output) = Program();
                    var rules = graph.DefineRuleset("rules");
                    rules.DefineRule("r", r =>
                    {
                        var (fn, y) = r.Vars<UnstableFn<long, long>, long>("fn", "y");
                        if (naive)
                            r.Naive();

                        if (inBody)
                        {
                            r.When(fn.Is(f.Of()), y.Is(app.Of(fn, 7L)));
                            r.Then(output.Of(0L).Is(y));
                        }
                        else
                        {
                            r.When(fn.Is(f.Of()));
                            r.Then(output.Of(0L).Is(app.Of(fn, 7L)));
                        }
                    });

                    if (naive)
                    {
                        graph.Root.Run(rules);
                        Assert.True(graph.Root.Check(output.Of(0L).Is(14L)));
                    }
                    else
                    {
                        var failure = Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(rules)).ToString();
                        Assert.Contains("unstable-fn over 'dbl'", failure, StringComparison.Ordinal);
                        Assert.Contains("Naive()", failure, StringComparison.Ordinal);
                    }
                }
            }
        }

        /// <summary>
        /// web-demo/unstable-fn.egg: a function applied in a rewrite's head, mapped over a list, partially applied to a
        /// pattern's class, composed, and over a primitive.
        /// </summary>
        [Fact]
        public void FunctionsMapOverAList()
        {
            // (datatype Math (Num i64) (Var String) (Add Math Math) (Mul Math Math)) (rewrite (Mul (Num x) (Num y)) (Num (* x y)))
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var mul = graph.DefineConstructor<Node, Node, Node>("Mul");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("fold", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.Rewrite(mul.Of(num.Of(x), num.Of(y)), num.Of(Primitives.Multiply.Of(x, y)));
            });

            // (datatype MathList (Nil) (Cons Math MathList)) (sort MathFn (UnstableFn (Math) Math))
            graph.DefineSort<NodeList>("MathList");
            var nil = graph.DefineConstructor<NodeList>("Nil");
            var cons = graph.DefineConstructor<Node, NodeList, NodeList>("Cons");
            graph.Values.RegisterFn<Node, Node>();
            var app = Primitives.UnstableApp<Node, Node>();

            // (constructor square (Math) Math) (rewrite (square x) (Mul x x)) (let $square-fn (unstable-fn "square"))
            var square = graph.DefineConstructor<Node, Node>("square");
            rules.DefineRule("square", r =>
            {
                var x = r.Var<Node>("x");
                r.Rewrite(square.Of(x), mul.Of(x, x));
            });
            var squareFn = graph.DefineFunction<UnstableFn<Node, Node>>("square-fn");
            graph.Root.Assume(squareFn.Of().Is(Primitives.UnstableFn<Node, Node>(square).Of()));

            // (let $squared-3 (unstable-app $square-fn (Num 3))) (check (= $squared-3 (square (Num 3))))
            var squared3 = graph.DefineFunction<Node>("squared-3");
            graph.Root.Assume(squared3.Of().Is(app.Of(squareFn.Of(), num.Of(3L))));
            Assert.True(graph.Root.AreEqual(graph.Root.Get(squared3.Of()), square.Of(num.Of(3L))));

            // (constructor list-map-math (MathList MathFn) MathList) (rewrite (list-map-math (Nil) fn) (Nil))
            // (rewrite (list-map-math (Cons x xs) fn) (Cons (unstable-app fn x) (list-map-math xs fn)))
            var listMap = graph.DefineConstructor<NodeList, UnstableFn<Node, Node>, NodeList>("list-map-math");
            rules.DefineRule("map-nil", r =>
            {
                var fn = r.Var<UnstableFn<Node, Node>>("fn");
                r.Rewrite(listMap.Of(nil.Of(), fn), nil.Of());
            });
            rules.DefineRule("map-cons", r =>
            {
                var (x, xs, fn) = r.Vars<Node, NodeList, UnstableFn<Node, Node>>("x", "xs", "fn");
                r.Rewrite(listMap.Of(cons.Of(x, xs), fn), cons.Of(app.Of(fn, x), listMap.Of(xs, fn)));
            });

            // (let $x (Cons (Num 1) (Cons (Num 2) (Cons (Num 3) (Nil))))) (let $squared-x (list-map-math $x $square-fn))
            ConstructorTerm<NodeList> List(params long[] values)
            {
                var list = nil.Of();
                for (var i = values.Length - 1; i >= 0; i--)
                    list = cons.Of(num.Of(values[i]), list);

                return list;
            }

            var x = graph.Root.Make(List(1, 2, 3));
            var squaredX = graph.Root.Make(listMap.Of(x, squareFn.Of()));
            graph.Root.Run(Schedule.Saturate(rules));
            Assert.True(graph.Root.AreEqual(squaredX, List(1, 4, 9)));

            // (constructor list-multiply-by (MathList Math) MathList) (rewrite (list-multiply-by l i) (list-map-math l (unstable-fn "Mul" i)))
            var multiplyBy = graph.DefineConstructor<NodeList, Node, NodeList>("list-multiply-by");
            var rule = rules.DefineRule("multiply", r =>
            {
                var (l, i) = r.Vars<NodeList, Node>("l", "i");
                r.Rewrite(multiplyBy.Of(l, i), listMap.Of(l, Primitives.UnstableFn<Node, Node>(mul).Of(i)));
            });
            Assert.Equal("(rule ((= $rw0 (list-multiply-by l i))) ((union $rw0 (list-map-math l (unstable-fn \"Mul\" i)))) :ruleset rules)", rule.ToString());

            // (let $doubled-x (list-multiply-by $x (Num 2)))
            var doubledX = graph.Root.Make(multiplyBy.Of(x, num.Of(2L)));
            graph.Root.Run(Schedule.Saturate(rules));
            Assert.True(graph.Root.AreEqual(doubledX, List(2, 4, 6)));

            // (constructor composed-math (MathFn MathFn Math) Math) (rewrite (composed-math f g v) (unstable-app f (unstable-app g v)))
            var composed = graph.DefineConstructor<UnstableFn<Node, Node>, UnstableFn<Node, Node>, Node, Node>("composed-math");
            rules.DefineRule("compose", r =>
            {
                var (f, g, v) = r.Vars<UnstableFn<Node, Node>, UnstableFn<Node, Node>, Node>("f", "g", "v");
                r.Rewrite(composed.Of(f, g, v), app.Of(f, app.Of(g, v)));
            });

            // (let $square-of-double (unstable-fn "composed-math" $square-fn (unstable-fn "Mul" (Num 2))))
            // (let $squared-doubled-x (list-map-math $x $square-of-double))
            var squareOfDouble = graph.DefineFunction<UnstableFn<Node, Node>>("square-of-double");
            graph.Root.Assume(squareOfDouble.Of().Is(Primitives.UnstableFn<Node, Node>(composed).Of(squareFn.Of(), Primitives.UnstableFn<Node, Node>(mul).Of(num.Of(2L)))));
            var squaredDoubledX = graph.Root.Make(listMap.Of(x, squareOfDouble.Of()));
            graph.Root.Run(Schedule.Saturate(rules));
            Assert.True(graph.Root.AreEqual(squaredDoubledX, List(4, 16, 36)));

            // (sort i64Fun (UnstableFn (i64) i64)) (constructor composed-i64-math (MathFn i64Fun i64) Math)
            // (rewrite (composed-i64-math f g v) (unstable-app f (Num (unstable-app g v))))
            graph.Values.RegisterFn<long, long>();
            var longApp = Primitives.UnstableApp<long, long>();
            var composedLong = graph.DefineConstructor<UnstableFn<Node, Node>, UnstableFn<long, long>, long, Node>("composed-i64-math");
            rules.DefineRule("compose-i64", r =>
            {
                var (f, g, v) = r.Vars<UnstableFn<Node, Node>, UnstableFn<long, long>, long>("f", "g", "v");
                r.Rewrite(composedLong.Of(f, g, v), app.Of(f, num.Of(longApp.Of(g, v))));
            });

            // (let $res (composed-i64-math $square-fn (unstable-fn "*" 2) 4)) (check (= $res (Num 64)))
            var res = graph.Root.Make(composedLong.Of(squareFn.Of(), Primitives.UnstableFn<long, long>(Primitives.Multiply).Of(2L), 4L));
            graph.Root.Run(Schedule.Saturate(rules));
            Assert.True(graph.Root.AreEqual(res, num.Of(64L)));

            // (sort TestNullaryFunction (UnstableFn () Math))
            Assert.Equal("UnstableFn<(), Math>", graph.Values.RegisterFn<Node>().Name);
        }

        /// <summary>
        /// repro-738-fn-sort.egg: a function value partially applied to a class extracts as egglog writes it.
        /// </summary>
        [Fact]
        public void AFunctionValueOfAClassExtracts()
        {
            // (sort X) (sort Y) (sort Y_to_X (UnstableFn (Y) X)) (constructor x () X) (constructor fn (X Y) X)
            var graph = new EGraph();
            graph.DefineSort<Node>("X");
            graph.DefineSort<NodeList>("Y");
            graph.Values.RegisterFn<NodeList, Node>();
            var x = graph.DefineConstructor<Node>("x");
            var fn = graph.DefineConstructor<Node, NodeList, Node>("fn");
            var holder = graph.DefineConstructor<UnstableFn<NodeList, Node>, Node>("holder");

            // (extract (unstable-fn "fn" (x)))
            var held = graph.Root.Make(holder.Of(Primitives.UnstableFn<NodeList, Node>(fn).Of(x.Of())));
            Assert.Equal("(holder (unstable-fn \"fn\" (x)))", graph.Root.Extract(held).ToString());
        }

    }

}
