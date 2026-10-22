using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// The higher-order container primitives over function values: the order a reduction folds in, the contexts their
    /// applications run in, rendering, and scopes.
    /// </summary>
    public sealed class HigherOrderTests
    {

        [Fact]
        public void AReductionFoldsInEgglogsOrder()
        {
            // egglog at 5e655cae, each a let and an extract: (unstable-multiset-reduce (unstable-fn "-") 0 (multiset-of 5 -1 3))
            // is -1, of (multiset-of 300 2 -7 1000000 -2) is -1000289, of (multiset-of 2 2 1) is -3, and
            // (unstable-multiset-reduce (unstable-fn "+") "" (multiset-of "b" "a" "c")) is "bac": its multiset keeps the
            // elements in the order of their representation, which the cells here are.
            var graph = new EGraph();
            graph.Values.RegisterMultiSet<long>();
            graph.Values.RegisterFn<long, long, long>();
            var reduce = Primitives.UnstableMultiSetReduce<long>();
            var minus = Primitives.UnstableFn<long, long, long>(Primitives.Subtract);
            var of = Primitives.MultiSetOf<long>();
            var r = graph.DefineFunction<long, long>("r");
            var root = graph.Root;
            root.Assume(r.Of(0L).Is(reduce.Of(minus.Of(), 0L, of.Of(5L, -1L, 3L))));
            root.Assume(r.Of(1L).Is(reduce.Of(minus.Of(), 0L, of.Of(300L, 2L, -7L, 1000000L, -2L))));
            root.Assume(r.Of(2L).Is(reduce.Of(minus.Of(), 0L, of.Of(2L, 2L, 1L))));
            Assert.Equal(-1L, root.Get(r.Of(0L)));
            Assert.Equal(-1000289L, root.Get(r.Of(1L)));
            Assert.Equal(-3L, root.Get(r.Of(2L)));

            graph.Values.RegisterMultiSet<string>();
            graph.Values.RegisterFn<string, string, string>();
            var s = graph.DefineFunction<string>("s");
            root.Assume(s.Of().Is(Primitives.UnstableMultiSetReduce<string>().Of(
                Primitives.UnstableFn<string, string, string>(StringPrimitives.Concat.WithArity(2)).Of(), "", Primitives.MultiSetOf<string>().Of("b", "a", "c"))));
            Assert.Equal("bac", root.Get(s.Of()));

            // A read folds in the same order over elements writes interned, and one no write interned last, as egglog's
            // check interns it: (check (= (unstable-multiset-reduce (unstable-fn "-") 0 (multiset-of 5 -12345 3)) 12343)).
            // egglog's check interns two such, (multiset-of -11 -13), in the order it meets them, which a read does not
            // know, so it has no result.
            Assert.True(root.Check(reduce.Of(minus.Of(), 0L, of.Of(5L, -1L, 3L)).Is(-1L)));
            Assert.True(root.Check(reduce.Of(minus.Of(), 0L, of.Of(5L, -12345L, 3L)).Is(12343L)));
            Assert.True(root.Check(reduce.Of(minus.Of(), 0L, of.Of(-12345L)).Is(-12345L)));
            var unordered = reduce.Of(minus.Of(), 0L, of.Of(-11L, -13L));
            Assert.False(root.Check(unordered.Is(unordered)));
        }

        [Fact]
        public void AMapsApplicationsRunWhereTheContextAllowsTheirFunction()
        {
            // (function dbl (i64) i64 :no-merge): a rule's body maps it only when the rule is naive, a read.
            static (EGraph Graph, Ruleset Rules, Function<long, long> Dbl, Function<Vec<long>> Out) Program()
            {
                var graph = new EGraph();
                graph.Values.RegisterVec<long>();
                graph.Values.RegisterFn<long, long>();
                var dbl = graph.DefineFunction<long, long>("dbl");
                graph.Root.Assume(dbl.Of(1L).Is(2L), dbl.Of(2L).Is(4L));
                return (graph, graph.DefineRuleset("rules"), dbl, graph.DefineFunction<Vec<long>>("out"));
            }

            var map = Primitives.UnstableVecMap<long, long>();
            foreach (var naive in new[] { true, false })
            {
                var (graph, rules, dbl, output) = Program();
                var rule = rules.DefineRule("map", r =>
                {
                    var v = r.Var<Vec<long>>("v");
                    if (naive)
                        r.Naive();

                    r.When(v.Is(map.Of(Primitives.UnstableFn<long, long>(dbl).Of(), Primitives.VecOf<long>().Of(1L, 2L, 3L))));
                    r.Then(output.Of().Is(v));
                });

                Assert.Equal(naive
                    ? "(rule ((= v (unstable-vec-map (unstable-fn \"dbl\") (vec-of 1 2 3)))) ((set (out) v)) :ruleset rules :naive)"
                    : "(rule ((= v (unstable-vec-map (unstable-fn \"dbl\") (vec-of 1 2 3)))) ((set (out) v)) :ruleset rules)", rule.ToString());
                if (naive)
                {
                    graph.Root.Run(rules);
                    Assert.Equal(Vec.Of(2L, 4L), graph.Root.Get(output.Of()));
                }
                else
                {
                    var failure = Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(rules)).ToString();
                    Assert.Contains("unstable-fn over 'dbl'", failure, StringComparison.Ordinal);
                }
            }
        }

        [Fact]
        public void AHeadMapsAConstructorIntoItsTerms()
        {
            // (rule ((= v (src))) ((set (out) (unstable-multiset-map (unstable-fn "Wrap") v)))): a head writes, so the
            // constructor makes its terms.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var wrap = graph.DefineConstructor<Node, Node>("Wrap");
            graph.Values.RegisterMultiSet<Node>();
            graph.Values.RegisterFn<Node, Node>();
            var of = Primitives.MultiSetOf<Node>();
            var src = graph.DefineFunction<MultiSet<Node>>("src");
            var output = graph.DefineFunction<MultiSet<Node>>("out");
            var root = graph.Root;
            root.Assume(src.Of().Is(of.Of(num.Of(1L), num.Of(1L), num.Of(2L))));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("wrap", r =>
            {
                var v = r.Var<MultiSet<Node>>("v");
                r.When(v.Is(src.Of()));
                r.Then(output.Of().Is(Primitives.UnstableMultiSetMap<Node, Node>().Of(Primitives.UnstableFn<Node, Node>(wrap).Of(), v)));
            });
            root.Run(rules);
            Assert.True(root.Check(output.Of().Is(of.Of(wrap.Of(num.Of(1L)), wrap.Of(num.Of(1L)), wrap.Of(num.Of(2L))))));
        }

        [Fact]
        public void AReductionWithNoResultDropsABodysMatchAndFailsAHead()
        {
            // (function f (i64 i64) i64 :no-merge) with no rows: the fold's first application has no result.
            var graph = new EGraph();
            graph.Values.RegisterMultiSet<long>();
            graph.Values.RegisterFn<long, long, long>();
            var f = graph.DefineFunction<long, long, long>("f");
            var output = graph.DefineFunction<long, long>("out");
            var reduce = Primitives.UnstableMultiSetReduce<long>();
            var fold = reduce.Of(Primitives.UnstableFn<long, long, long>(f).Of(), 0L, Primitives.MultiSetOf<long>().Of(1L, 2L));
            var body = graph.DefineRuleset("body");
            body.DefineRule("body", r =>
            {
                var y = r.Var<long>("y");
                r.Naive();
                r.When(y.Is(fold));
                r.Then(output.Of(0L).Is(y));
            });
            var head = graph.DefineRuleset("head");
            head.DefineRule("head", r =>
            {
                r.Naive();
                r.Then(output.Of(1L).Is(fold));
            });

            graph.Root.Run(body);
            Assert.Equal(0, graph.Root.CountRows(output));
            Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(head));
        }

        [Fact]
        public void AFailedFactUndoesTheTermsItsMapMade()
        {
            // (set (out) (multiset-of (Num 9))) then a fact that sets (out) to a map over Wrap: the fact conflicts, and
            // the terms its map made go with it.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var wrap = graph.DefineConstructor<Node, Node>("Wrap");
            graph.Values.RegisterMultiSet<Node>();
            graph.Values.RegisterFn<Node, Node>();
            var of = Primitives.MultiSetOf<Node>();
            var output = graph.DefineFunction<MultiSet<Node>>("out");
            var root = graph.Root;
            root.Assume(output.Of().Is(of.Of(num.Of(9L))));
            var map = Primitives.UnstableMultiSetMap<Node, Node>().Of(Primitives.UnstableFn<Node, Node>(wrap).Of(), of.Of(num.Of(1L)));
            Assert.ThrowsAny<Exception>(() => root.Assume(output.Of().Is(map)));
            Assert.False(root.Check(Fact.Exists(wrap.Of(num.Of(1L)))));
            Assert.True(root.Check(output.Of().Is(of.Of(num.Of(9L)))));
        }

        [Fact]
        public void AMapInABatchSeesTheBatchsEarlierMerges()
        {
            // (union (Num 1) (Num 2)) then (set (out) (unstable-multiset-map (unstable-fn "Wrap") (multiset-of (Num 1) (Num 2)))),
            // in one call: the map runs after the union, as egglog's next command would, so the two are one element.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var wrap = graph.DefineConstructor<Node, Node>("Wrap");
            graph.Values.RegisterMultiSet<Node>();
            graph.Values.RegisterFn<Node, Node>();
            var of = Primitives.MultiSetOf<Node>();
            var output = graph.DefineFunction<MultiSet<Node>>("out");
            var root = graph.Root;
            root.Assume(
                num.Of(1L).Is(num.Of(2L)),
                output.Of().Is(Primitives.UnstableMultiSetMap<Node, Node>().Of(Primitives.UnstableFn<Node, Node>(wrap).Of(), of.Of(num.Of(1L), num.Of(2L)))));
            Assert.Equal(2L, root.Get(Primitives.MultiSetCount<Node>().Of(output.Of(), wrap.Of(num.Of(1L)))));
            Assert.Equal(1L, root.Get(Primitives.MultiSetLength<Node>().Of(Primitives.MultiSetResetCounts<Node>().Of(output.Of()))));
        }

        [Fact]
        public void AChildMapsValues()
        {
            var graph = new EGraph();
            graph.Values.RegisterVec<long>();
            graph.Values.RegisterFn<long, long>();
            var output = graph.DefineFunction<Vec<long>>("out");
            graph.Root.Freeze();
            using var child = graph.Root.CreateChild();
            child.Assume(output.Of().Is(Primitives.UnstableVecMap<long, long>().Of(Primitives.UnstableFn<long, long>(Primitives.Add).Of(10L), Primitives.VecOf<long>().Of(1L, 2L))));
            Assert.Equal(Vec.Of(11L, 12L), child.Get(output.Of()));
        }

    }

}
