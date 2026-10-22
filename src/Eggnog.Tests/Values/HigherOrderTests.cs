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

            // A read folds in the same order over elements writes interned, and has no result where one no write
            // interned would set it.
            Assert.True(root.Check(reduce.Of(minus.Of(), 0L, of.Of(5L, -1L, 3L)).Is(-1L)));
            Assert.False(root.Check(reduce.Of(minus.Of(), 0L, of.Of(5L, -12345L, 3L)).Is(reduce.Of(minus.Of(), 0L, of.Of(5L, -12345L, 3L)))));
            Assert.True(root.Check(reduce.Of(minus.Of(), 0L, of.Of(-12345L)).Is(-12345L)));
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
