using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Multisets of values, egglog's <c>MultiSet</c> container: the multiset type, registration, its primitives against
    /// what egglog's give at 5e655cae, the picks that read cells, rules, rendering, serialization and extraction.
    /// </summary>
    public sealed class MultiSetTests
    {

        [Fact]
        public void AMultiSetCountsItsElements()
        {
            var m = MultiSet.Of("b", "a", "a");
            Assert.Equal(3, m.Count);
            Assert.Equal(2, m.CountOf("a"));
            Assert.Equal(0, m.CountOf("c"));
            Assert.Equal(new[] { "a", "a", "b" }, m);
            Assert.Equal(m, MultiSet.Of("a", "b", "a"));
            Assert.NotEqual(m, MultiSet.Of("a", "b"));
            Assert.Equal(MultiSet.Of("a", "b"), m.Remove("a"));
            Assert.Same(m, m.Remove("c"));
            Assert.True(m.TrySubtract(MultiSet.Of("a", "b"), out var left));
            Assert.Equal(MultiSet.Of("a"), left);
            Assert.False(m.TrySubtract(MultiSet.Of("b", "b"), out _));
            Assert.Equal(MultiSet.Of("a", "a", "a", "b", "c"), m.Sum(MultiSet.Of("c", "a")));
            Assert.Equal(MultiSet.Of("a"), m.Intersect(MultiSet.Of("a", "c")));
            Assert.Equal(MultiSet.Of("a", "b"), m.ResetCounts());
            Assert.Equal("{a, a, b}", m.ToString());
        }

        [Fact]
        public void ThePrimitivesAreEgglogs()
        {
            // (sort MS (MultiSet String)) (let $m (multiset-of "b" "a" "a")) and egglog's checks: multiset-pick is the
            // element of least cell, "b", interned first; multiset-pick-max of a tie the last in cell order.
            var t = new NodeTheory();
            Assert.Equal("MultiSet<String>", t.Graph.Values.RegisterMultiSet<string>().Name);
            var of = Primitives.MultiSetOf<string>();
            var m = t.Graph.DefineFunction<MultiSet<string>>("m");
            t.Root.Assume(m.Of().Is(of.Of("b", "a", "a")));

            Assert.True(t.Root.Check(Primitives.MultiSetPick<string>().Of(m.Of()).Is("b")));
            Assert.True(t.Root.Check(Primitives.MultiSetPickMax<string>().Of(m.Of()).Is("a")));
            Assert.True(t.Root.Check(Primitives.MultiSetPickMax<string>().Of(of.Of("b", "a")).Is("a")));
            Assert.True(t.Root.Check(Primitives.MultiSetLength<string>().Of(m.Of()).Is(3L)));
            Assert.True(t.Root.Check(Primitives.MultiSetCount<string>().Of(m.Of(), "a").Is(2L)));
            Assert.True(t.Root.Check(Primitives.MultiSetSingle<string>().Of("z", 0L).Is(of.Of())));

            // (datatype N (Leaf MS)) (extract (Leaf $m)) prints (Leaf (multiset-of "b" "a" "a")).
            var leaf = t.Graph.DefineConstructor<MultiSet<string>, Node>("Leaf");
            var x = t.Root.Make(leaf.Of(m.Of()));
            Assert.Equal("(Leaf (multiset-of \"b\" \"a\" \"a\"))", t.Root.Extract(x).ToString());
            Assert.Equal(1 + 3, t.Root.Extract(x).Cost);

            // (sort IM (MultiSet i64)) (sort MM (MultiSet IM)) and multiset-sum-multisets, multiset-reset-counts.
            t.Graph.Values.RegisterMultiSet<long>();
            t.Graph.Values.RegisterMultiSet<MultiSet<long>>();
            var ints = Primitives.MultiSetOf<long>();
            var outer = Primitives.MultiSetOf<MultiSet<long>>().Of(ints.Of(1L, 2L), ints.Of(1L, 2L), ints.Of(3L));
            Assert.True(t.Root.Check(Primitives.MultiSetSumMultiSets<long>().Of(outer).Is(ints.Of(1L, 1L, 2L, 2L, 3L))));
            Assert.Equal(MultiSet.Of(1L, 3L), t.Root.Get(Primitives.MultiSetResetCounts<long>().Of(ints.Of(3L, 3L, 1L))));
        }

        [Fact]
        public void APickOverElementsNoWriteInternedHasNoResult()
        {
            // A read never interns, and egglog's check would intern the elements in its own order, so of two elements
            // with no cell which egglog picks first is unknown here: no result, as ordering-min gives none.
            var graph = new EGraph();
            graph.Values.RegisterMultiSet<string>();
            var pick = Primitives.MultiSetPick<string>();
            var of = Primitives.MultiSetOf<string>();
            Assert.False(graph.Root.TryGet(pick.Of(of.Of("fresh-y", "fresh-x")), out _));
            Assert.Equal("only", graph.Root.Get(pick.Of(of.Of("only"))));
            var f = graph.DefineFunction<string>("f");
            graph.Root.Assume(f.Of().Is("known"));
            Assert.Equal("known", graph.Root.Get(pick.Of(of.Of("fresh", "known"))));
        }

        [Fact]
        public void PartialMultiSetPrimitivesDropAndFail()
        {
            var graph = new EGraph();
            graph.Values.RegisterMultiSet<long>();
            var m = graph.DefineFunction<long, MultiSet<long>>("m");
            var removed = graph.DefineFunction<long, MultiSet<long>>("removed");
            var picked = graph.DefineFunction<long, long>("picked");
            var body = graph.DefineRuleset("body");
            var rule = body.DefineRule("remove", r =>
            {
                var (k, x) = r.Vars<long, MultiSet<long>>("k", "x");
                var y = r.Var<MultiSet<long>>("y");
                r.When(x.Is(m.Of(k)), y.Is(Primitives.MultiSetRemove<long>().Of(x, 5L)));
                r.Then(removed.Of(k).Is(y), picked.Of(k).Is(Primitives.MultiSetPick<long>().Of(x)));
            });
            graph.Root.Assume(m.Of(1L).Is(MultiSet.Of(5L, 7L)), m.Of(2L).Is(MultiSet.Of(7L)));

            graph.Root.Run(Schedule.Run(body));

            Assert.Equal(MultiSet.Of(7L), graph.Root.Get(removed.Of(1L)));
            Assert.Equal(5L, graph.Root.Get(picked.Of(1L)));
            Assert.False(graph.Root.TryGet(removed.Of(2L), out _));
            Assert.Equal(
                "(rule ((= x (m k)) (= y (multiset-remove x 5))) ((set (removed k) y) (set (picked k) (multiset-pick x))) :ruleset body)",
                rule.ToString());

            var head = graph.DefineRuleset("head");
            head.DefineRule("pick", r =>
            {
                var (k, x) = r.Vars<long, MultiSet<long>>("k", "x");
                r.When(x.Is(m.Of(k)));
                r.Then(picked.Of(k).Is(Primitives.MultiSetPick<long>().Of(x)));
            });
            graph.Root.Assume(m.Of(3L).Is(MultiSet<long>.Empty));
            Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(Schedule.Run(head)));
        }

        [Fact]
        public void RegistrationRefusesClassesAndIncomparableElements()
        {
            var t = new NodeTheory();
            var sort = Assert.Throws<ArgumentException>(() => t.Graph.Values.RegisterMultiSet<Node>());
            Assert.Contains("Node is a sort", sort.Message, StringComparison.Ordinal);
            t.Graph.Values.Register<NumericBound>("NumericBound");
            var element = Assert.Throws<ArgumentException>(() => t.Graph.Values.RegisterMultiSet<NumericBound>());
            Assert.Contains("IComparable", element.Message, StringComparison.Ordinal);
            t.Graph.Values.RegisterVec<NumericBound>();
            Assert.Throws<ArgumentException>(() => t.Graph.Values.RegisterMultiSet<Vec<NumericBound>>());
            t.Graph.Values.RegisterVec<long>();
            Assert.Equal("MultiSet<Vec<i64>>", t.Graph.Values.RegisterMultiSet<Vec<long>>().Name);
            var register = Assert.Throws<ArgumentException>(() => t.Graph.Values.Register<MultiSet<long>>("M"));
            Assert.Contains("ValueStore.RegisterMultiSet", register.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AMultiSetSerializesItsElementsWithRepeatsInCellOrder()
        {
            var graph = new EGraph();
            graph.Values.RegisterMultiSet<string>();
            var m = graph.DefineFunction<MultiSet<string>>("m");
            graph.Root.Assume(m.Of().Is(Primitives.MultiSetOf<string>().Of("b", "a", "a")));

            var serialized = graph.Root.Serialize();
            var node = serialized.Nodes.Single(n => n.Op == "multiset-of");
            var children = node.Children.Select(c => serialized.Nodes.Single(n => n.Id == c).Op).ToArray();
            Assert.Equal(new[] { "\"b\"", "\"a\"", "\"a\"" }, children);
        }

    }

}
