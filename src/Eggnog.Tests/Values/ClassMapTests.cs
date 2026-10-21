using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Maps and multisets of classes, egglog's eq-sort containers: keys that become one collapse, the later in key-cell
    /// order kept, and elements that become one add their counts, as egglog's rebuild at 5e655cae makes them.
    /// </summary>
    public sealed class ClassMapTests
    {

        sealed class Theory
        {

            public Theory()
            {
                Graph.DefineSort<Node>("Math");
                Num = Graph.DefineConstructor<long, Node>("Num");
                Graph.Values.RegisterMap<Node, long>();
                Graph.Values.RegisterMultiSet<Node>();
            }

            public EGraph Graph { get; } = new();

            public Scope Root => Graph.Root;

            public Constructor<long, Node> Num { get; }

            public Term<Map<Node, long>> Insert(Term<Map<Node, long>> map, long key, long value) =>
                Primitives.MapInsert<Node, long>().Of(map, Num.Of(key), value);

            public Term<Map<Node, long>> Empty => Primitives.MapEmpty<Node, long>().Of();

            public Node Make(long n) => Root.Make(Num.Of(n));

        }

        [Fact]
        public void KeysThatBecomeOneCollapseAndCountsAdd()
        {
            // (set (m) (map-insert (map-insert (map-empty) (Num 1) 10) (Num 2) 20)) (set (s) (multiset-of (Num 1) (Num 2) (Num 2)))
            // (union (Num 1) (Num 2)): m is (map-insert (map-empty) (Num 1) 20), the later key's value, and s holds (Num 1)
            // three times.
            var t = new Theory();
            var m = t.Graph.DefineFunction<Map<Node, long>>("m");
            var s = t.Graph.DefineFunction<MultiSet<Node>>("s");
            t.Root.Assume(
                m.Of().Is(t.Insert(t.Insert(t.Empty, 1L, 10L), 2L, 20L)),
                s.Of().Is(Primitives.MultiSetOf<Node>().Of(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(2L))));

            t.Root.Union(t.Make(1L), t.Make(2L));

            Assert.True(t.Root.Check(Primitives.MapLength<Node, long>().Of(m.Of()).Is(1L)));
            Assert.True(t.Root.Check(Primitives.MapGet<Node, long>().Of(m.Of(), t.Num.Of(1L)).Is(20L)));
            Assert.True(t.Root.Check(Primitives.MultiSetLength<Node>().Of(s.Of()).Is(3L)));
            Assert.True(t.Root.Check(Primitives.MultiSetCount<Node>().Of(s.Of(), t.Num.Of(2L)).Is(3L)));
            var one = t.Root.Find(t.Make(1L));
            Assert.Equal(Map.Of((one, 20L)), t.Root.Get(m.Of()));
            Assert.Equal(MultiSet.Of(one, one, one), t.Root.Get(s.Of()));
        }

        [Fact]
        public void TheLaterKeyInCellOrderIsKept()
        {
            // (map-insert (map-insert (map-empty) (Num 2) 20) (Num 1) 10): (Num 2) has the smaller cell, so (Num 1)'s 10 is
            // kept, under (Num 2), the class's cell.
            var t = new Theory();
            var m = t.Graph.DefineFunction<Map<Node, long>>("m");
            t.Root.Assume(m.Of().Is(t.Insert(t.Insert(t.Empty, 2L, 20L), 1L, 10L)));

            t.Root.Union(t.Make(1L), t.Make(2L));

            Assert.Equal(Map.Of((t.Root.Find(t.Make(2L)), 10L)), t.Root.Get(m.Of()));
        }

        [Fact]
        public void MapsAndMultisetsOfClassesSerializeAndExtractAsEgglogs()
        {
            // (constructor L (MM) Math) (constructor S (MS) Math) (L (map-insert (map-insert (map-empty) (Num 1) 10) (Num 2) 20))
            // (S (multiset-of (Num 2) (Num 1) (Num 2))), extracted, then (union (Num 1) (Num 2)), serialized and extracted.
            var t = new Theory();
            var l = t.Graph.DefineConstructor<Map<Node, long>, Node>("L");
            var s = t.Graph.DefineConstructor<MultiSet<Node>, Node>("S");
            var x = t.Root.Make(l.Of(t.Insert(t.Insert(t.Empty, 1L, 10L), 2L, 20L)));
            var y = t.Root.Make(s.Of(Primitives.MultiSetOf<Node>().Of(t.Num.Of(2L), t.Num.Of(1L), t.Num.Of(2L))));
            Assert.Equal("(L (map-insert (map-insert (map-empty) (Num 1) 10) (Num 2) 20))", t.Root.Extract(x).ToString());
            Assert.Equal("(S (multiset-of (Num 1) (Num 2) (Num 2)))", t.Root.Extract(y).ToString());

            t.Root.Union(t.Make(1L), t.Make(2L));

            Assert.Equal("(L (map-insert (map-empty) (Num 1) 20))", t.Root.Extract(x).ToString());
            Assert.Equal("(S (multiset-of (Num 1) (Num 1) (Num 1)))", t.Root.Extract(y).ToString());
            var serialized = t.Root.Serialize();
            var nodes = serialized.Nodes.Select(n => $"{n.Id} {n.Op} [{string.Join(", ", n.Children)}] {n.Class}");
            Assert.Equal(
                new[]
                {
                    "primitive-i64-1 1 [] i64-1",
                    "function-0-Num Num [primitive-i64-1] Math-1",
                    "primitive-i64-2 2 [] i64-2",
                    "function-1-Num Num [primitive-i64-2] Math-1",
                    "primitive-i64-20 20 [] i64-20",
                    "primitive-Map<Math, i64>-4 Map<Math, i64> [function-1-Num, primitive-i64-20] Map<Math, i64>-4",
                    "function-0-L L [primitive-Map<Math, i64>-4] Math-5",
                    "primitive-MultiSet<Math>-6 multiset-of [function-0-Num, function-1-Num, function-0-Num] MultiSet<Math>-6",
                    "function-0-S S [primitive-MultiSet<Math>-6] Math-7",
                },
                nodes);
        }

        [Fact]
        public void AMergeThatCollapsesTwoKeysConflicts()
        {
            // (function g (MM) i64 :no-merge): the keys {(Num 1): 1, (Num 2): 1} and {(Num 1): 1} become one map when
            // (Num 1) and (Num 2) are one class, so the rows meet with two values, which egglog's rebuild panics on.
            var t = new Theory();
            var g = t.Graph.DefineFunction<Map<Node, long>, long>("g");
            t.Root.Assume(
                g.Of(t.Insert(t.Insert(t.Empty, 1L, 1L), 2L, 1L)).Is(10L),
                g.Of(t.Insert(t.Empty, 1L, 1L)).Is(20L));

            Assert.Throws<FactConflictException>(() => t.Root.Union(t.Make(1L), t.Make(2L)));
            Assert.False(t.Root.IsFaulted);

            // A multiset's rows meet when their counts add up to one multiset.
            var h = t.Graph.DefineFunction<MultiSet<Node>, long>("h");
            var of = Primitives.MultiSetOf<Node>();
            t.Root.Assume(h.Of(of.Of(t.Num.Of(3L), t.Num.Of(4L))).Is(1L), h.Of(of.Of(t.Num.Of(4L), t.Num.Of(4L))).Is(2L));
            Assert.Throws<FactConflictException>(() => t.Root.Union(t.Make(3L), t.Make(4L)));
        }

        [Fact]
        public void AConstantMapOfStaleClassesCollapses()
        {
            var t = new Theory();
            var m = t.Graph.DefineFunction<long, Map<Node, long>>("m");
            var (one, two) = (t.Make(1L), t.Make(2L));
            t.Root.Union(one, two);

            // Both keys are one class now: the later, two, keeps its value, as a rebuild keeps the later key's.
            t.Root.Assume(m.Of(1L).Is(Map.Of((one, 10L), (two, 20L))));
            Assert.Equal(Map.Of((t.Root.Find(one), 20L)), t.Root.Get(m.Of(1L)));
            Assert.True(t.Root.Check(m.Of(1L).Is(Map.Of((one, 10L), (two, 20L)))));
            Assert.True(t.Root.Check(m.Of(1L).Is(Map.Of((two, 20L)))));
        }

        [Fact]
        public void MultisetUnionValuesUnionsItsElements()
        {
            // (multiset-union-values (multiset-of (Num 5) (Num 6) (Num 7))) is (Num 5), and the three are one class.
            var t = new Theory();
            var first = t.Graph.DefineFunction<Node>("first");
            var of = Primitives.MultiSetOf<Node>();
            t.Root.Assume(first.Of().Is(Primitives.MultiSetUnionValues<Node>().Of(of.Of(t.Num.Of(5L), t.Num.Of(6L), t.Num.Of(7L)))));

            Assert.True(t.Root.AreEqual(t.Make(5L), t.Make(7L)));
            Assert.True(t.Root.AreEqual(t.Make(6L), t.Make(5L)));
            Assert.True(t.Root.AreEqual(t.Root.Get(first.Of()), t.Make(5L)));
            Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(first.Of().Is(Primitives.MultiSetUnionValues<Node>().Of(of.Of()))));
        }

        [Fact]
        public void NestedAndValueSidedContainersOfClassesRebuild()
        {
            // A multiset of vectors of classes, and a map whose values are classes.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterMultiSet<Vec<Node>>();
            graph.Values.RegisterMap<long, Node>();
            var s = graph.DefineFunction<MultiSet<Vec<Node>>>("s");
            var m = graph.DefineFunction<Map<long, Node>>("m");
            var vec = Primitives.VecOf<Node>();
            var root = graph.Root;
            root.Assume(
                s.Of().Is(Primitives.MultiSetOf<Vec<Node>>().Of(vec.Of(num.Of(1L)), vec.Of(num.Of(2L)))),
                m.Of().Is(Primitives.MapInsert<long, Node>().Of(Primitives.MapEmpty<long, Node>().Of(), 7L, num.Of(2L))));

            root.Union(root.Make(num.Of(1L)), root.Make(num.Of(2L)));

            var one = root.Find(root.Make(num.Of(1L)));
            Assert.Equal(MultiSet.Of(Vec.Of(one), Vec.Of(one)), root.Get(s.Of()));
            Assert.Equal(Map.Of((7L, one)), root.Get(m.Of()));
            Assert.Throws<ArgumentException>(() => graph.Values.RegisterSet<Node>());
        }

    }

}
