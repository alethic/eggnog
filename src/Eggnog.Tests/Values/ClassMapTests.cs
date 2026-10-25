using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Maps and multisets of classes, egglog's eq-sort containers: keys that become one collapse, the later in key-cell
    /// order kept, and elements that become one add their counts, as egglog's rebuild at 5e655cae makes them, at the root
    /// and in a child's copies, as egglog's push makes them.
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
        public void KeysThatBecomeOneInAChildCollapseThereOnly()
        {
            // Probe dirty-parent-row.egg, its second block, which egglog at 5e655cae runs clean: a map made before the
            // push, (map-insert (map-insert (map-empty) (c1) 10) (c2) 20), held by (p $m); in the push (union (c1)
            // (c2)), here a rule's, collapses its keys, (check (= (map-length $m) 1)); after the pop it has two again.
            var t = new Theory();
            var m = t.Graph.DefineFunction<Map<Node, long>>("m");
            var p = t.Graph.DefineConstructor<Map<Node, long>, Node>("p");
            var go = t.Graph.DefineRelation("go");
            var merge = t.Graph.DefineRuleset("merge");
            merge.DefineRule("merge", r =>
            {
                r.When(go.Of());
                r.Then(t.Num.Of(1L).Is(t.Num.Of(2L)));
            });
            t.Root.Assume(m.Of().Is(t.Insert(t.Insert(t.Empty, 1L, 10L), 2L, 20L)));
            var held = t.Root.Make(p.Of(m.Of()));
            t.Root.Freeze();

            using (var child = t.Root.CreateChild())
            {
                child.Assume(go.Of());
                child.Run(Schedule.Saturate(Schedule.Run(merge)));
                Assert.True(child.Check(Primitives.MapLength<Node, long>().Of(m.Of()).Is(1L)));
                Assert.True(child.Check(Primitives.MapGet<Node, long>().Of(m.Of(), t.Num.Of(1L)).Is(20L)));
                Assert.Equal(Map.Of((child.Find(child.Make(t.Num.Of(1L))), 20L)), child.Get(m.Of()));
                Assert.True(child.Check(p.Of(m.Of()).Is(held)));
            }

            Assert.True(t.Root.Check(Primitives.MapLength<Node, long>().Of(m.Of()).Is(2L)));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void KeysThatBecomeOneInAChildsHostMergeCollapseAndCountsAddThereOnly(bool frozen)
        {
            // Probe map-child.egg, which egglog at 5e655cae runs clean: the program of
            // KeysThatBecomeOneCollapseAndCountsAdd with (union (Num 1) (Num 2)) in a push, where (m) is (map-insert
            // (map-empty) (Num 1) 20) and (s) (multiset-of (Num 1) (Num 1) (Num 1)); after the pop (m) has two keys and
            // (s) counts (Num 2) twice. Here the union is the child's host merge, which displaces (Num 2), a key of the
            // map and an element of the multiset.
            var t = new Theory();
            var m = t.Graph.DefineFunction<Map<Node, long>>("m");
            var s = t.Graph.DefineFunction<MultiSet<Node>>("s");
            t.Root.Assume(
                m.Of().Is(t.Insert(t.Insert(t.Empty, 1L, 10L), 2L, 20L)),
                s.Of().Is(Primitives.MultiSetOf<Node>().Of(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(2L))));
            var (one, two) = (t.Make(1L), t.Make(2L));
            if (frozen)
                t.Root.Freeze();

            using (var child = t.Root.CreateChild())
            {
                child.Union(one, two);
                Assert.False(child.IsFaulted);
                Assert.True(child.Check(Primitives.MapLength<Node, long>().Of(m.Of()).Is(1L)));
                Assert.True(child.Check(Primitives.MapGet<Node, long>().Of(m.Of(), t.Num.Of(1L)).Is(20L)));
                Assert.True(child.Check(Primitives.MultiSetLength<Node>().Of(s.Of()).Is(3L)));
                Assert.True(child.Check(Primitives.MultiSetCount<Node>().Of(s.Of(), t.Num.Of(2L)).Is(3L)));
                Assert.True(child.Check(m.Of().Is(t.Insert(t.Empty, 1L, 20L))));
                Assert.True(child.Check(s.Of().Is(Primitives.MultiSetOf<Node>().Of(t.Num.Of(1L), t.Num.Of(1L), t.Num.Of(1L)))));
                Assert.Equal(Map.Of((one, 20L)), child.Get(m.Of()));
                Assert.Equal(MultiSet.Of(one, one, one), child.Get(s.Of()));
                child.Engine.Context.CheckInvariants();
            }

            Assert.True(t.Root.Check(Primitives.MapLength<Node, long>().Of(m.Of()).Is(2L)));
            Assert.True(t.Root.Check(Primitives.MultiSetCount<Node>().Of(s.Of(), t.Num.Of(2L)).Is(2L)));
            Assert.False(t.Root.AreEqual(one, two));
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
        public void AChildsMergeThatCollapsesTwoKeysConflictsAsTheRootsDoes()
        {
            // Probe map-child-conflict.egg: the program of AMergeThatCollapsesTwoKeysConflicts with each union in a
            // push of its own, where egglog at 5e655cae panics as at its root, which (fail ...) catches; g and h keep
            // their two rows each. The child's batch checks the merge against the maps and multisets of its view and
            // refuses it, as the root's does, before anything is written.
            var t = new Theory();
            var g = t.Graph.DefineFunction<Map<Node, long>, long>("g");
            var h = t.Graph.DefineFunction<MultiSet<Node>, long>("h");
            var of = Primitives.MultiSetOf<Node>();
            t.Root.Assume(
                g.Of(t.Insert(t.Insert(t.Empty, 1L, 1L), 2L, 1L)).Is(10L),
                g.Of(t.Insert(t.Empty, 1L, 1L)).Is(20L));
            t.Root.Assume(h.Of(of.Of(t.Num.Of(3L), t.Num.Of(4L))).Is(1L), h.Of(of.Of(t.Num.Of(4L), t.Num.Of(4L))).Is(2L));
            var (one, two, three, four) = (t.Make(1L), t.Make(2L), t.Make(3L), t.Make(4L));
            t.Root.Freeze();

            using (var child = t.Root.CreateChild())
            {
                Assert.Throws<FactConflictException>(() => child.Union(one, two));
                Assert.Throws<FactConflictException>(() => child.Union(three, four));
                Assert.False(child.IsFaulted);
                Assert.False(child.AreEqual(one, two));
                Assert.False(child.AreEqual(three, four));
                Assert.Equal(2, child.CountRows(g));
                Assert.Equal(2, child.CountRows(h));
            }

            Assert.Equal(2, t.Root.CountRows(g));
            Assert.Equal(2, t.Root.CountRows(h));
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

            // web-demo/multiset.egg: (push) (multiset-union-values $xs) (check (= (Num 1) (Num 2))) (pop), run for its
            // effect alone.
            t.Root.Assume(Fact.Exists(Primitives.MultiSetUnionValues<Node>().Of(of.Of(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L)))));
            Assert.True(t.Root.AreEqual(t.Make(1L), t.Make(2L)));
            Assert.True(t.Root.AreEqual(t.Make(3L), t.Make(2L)));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MultisetUnionValuesUnionsItsElementsInAChild(bool frozen)
        {
            // Probe multiset-union-values.egg, which egglog at 5e655cae runs clean: web-demo/multiset.egg's block,
            // (push) (multiset-union-values $xs) (check (= (Num 1) (Num 2))) (pop), over $xs = (multiset-of (Num 1)
            // (Num 2) (Num 3)), and h of (multiset-of (Num 1) (Num 1) (Num 1)) 5 and of $xs 1 under max: in a push run for
            // its effect alone, the three are one, $xs counts (Num 1) three times and is the other multiset, so h has
            // one row, 5; in a push as the value of (set (first) ...) it is (Num 1); and in a push by a rule's head too.
            // After each pop they are apart and h has two rows. An empty multiset has no result.
            var t = new Theory();
            var xs = t.Graph.DefineFunction<MultiSet<Node>>("xs");
            var none = t.Graph.DefineFunction<MultiSet<Node>>("none");
            var first = t.Graph.DefineFunction<Node>("first");
            var h = t.Graph.DefineFunction<MultiSet<Node>, long>("h", ValueLattice.Max<long>());
            var of = Primitives.MultiSetOf<Node>();
            var union = Primitives.MultiSetUnionValues<Node>();
            var rules = t.Graph.DefineRuleset("rules");
            rules.DefineRule("union", r =>
            {
                var x = r.Var<MultiSet<Node>>("x");
                r.When(x.Is(xs.Of()));
                r.Then(first.Of().Is(union.Of(x)));
            });
            t.Root.Assume(xs.Of().Is(of.Of(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L))), none.Of().Is(of.Of()));
            t.Root.Assume(h.Of(of.Of(t.Num.Of(1L), t.Num.Of(1L), t.Num.Of(1L))).Is(5L), h.Of(xs.Of()).Is(1L));
            var (one, two, three) = (t.Make(1L), t.Make(2L), t.Make(3L));
            if (frozen)
                t.Root.Freeze();

            using (var child = t.Root.CreateChild())
            {
                child.Assume(Fact.Exists(union.Of(xs.Of())));
                Assert.True(child.AreEqual(one, two));
                Assert.True(child.AreEqual(three, two));
                Assert.True(child.Check(Primitives.MultiSetCount<Node>().Of(xs.Of(), t.Num.Of(1L)).Is(3L)));
                Assert.True(child.Check(xs.Of().Is(of.Of(t.Num.Of(1L), t.Num.Of(1L), t.Num.Of(1L)))));
                Assert.Equal(5L, child.Get(h.Of(xs.Of())));
                Assert.Equal(1, child.CountRows(h));
                Assert.Throws<RuleEvaluationException>(() => child.Assume(first.Of().Is(union.Of(none.Of()))));
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            using (var child = t.Root.CreateChild())
            {
                child.Assume(first.Of().Is(union.Of(xs.Of())));
                Assert.True(child.AreEqual(child.Get(first.Of()), one));
                Assert.True(child.AreEqual(three, two));
            }

            using (var child = t.Root.CreateChild())
            {
                child.Run(rules);
                Assert.True(child.AreEqual(child.Get(first.Of()), one));
                Assert.True(child.AreEqual(three, one));
                Assert.Equal(5L, child.Get(h.Of(xs.Of())));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(t.Root.AreEqual(one, two));
            Assert.Equal(1L, t.Root.Get(h.Of(xs.Of())));
            Assert.Equal(2, t.Root.CountRows(h));
            Assert.False(t.Root.TryGet(first.Of(), out _));
        }

        sealed class Passes
        {

            // (Num 1) #0, (Num 10) #1, (Num 20) #2, (G (Num 10)) #3, (G (Num 20)) #4, and M = {#0: 1, #3: 2, #4: 3}; one
            // batch of (union (Num 1) (G (Num 20))) (union (Num 10) (Num 20)) collapses #0 and #4 in the rebuild's first
            // pass, keeping 3, and #3 into them in the second, by G's congruence, keeping 2: M is {(Num 1): 2}, as
            // egglog's and the engine's rebuild make it.
            public Passes()
            {
                Graph.DefineSort<Node>("Math");
                Num = Graph.DefineConstructor<long, Node>("Num");
                G = Graph.DefineConstructor<Node, Node>("G");
                Graph.Values.RegisterMap<Node, long>();
                F = Graph.DefineFunction<Map<Node, long>, long>("f");
                K1 = Root.Make(Num.Of(1L));
                A = Root.Make(Num.Of(10L));
                B = Root.Make(Num.Of(20L));
                K2 = Root.Make(G.Of(Num.Of(10L)));
                K3 = Root.Make(G.Of(Num.Of(20L)));
            }

            public EGraph Graph { get; } = new();

            public Scope Root => Graph.Root;

            public Constructor<long, Node> Num { get; }

            public Constructor<Node, Node> G { get; }

            public Function<Map<Node, long>, long> F { get; }

            public Node K1 { get; }

            public Node A { get; }

            public Node B { get; }

            public Node K2 { get; }

            public Node K3 { get; }

            public Map<Node, long> M => Map.Of((K1, 1L), (K2, 2L), (K3, 3L));

            public void Merge() => MergeIn(Root);

            public void MergeIn(Scope scope) => scope.Write(w =>
            {
                w.Union(K1, K3);
                w.Union(A, B);
            });

        }

        [Fact]
        public void AMapCollapsesPassByPass()
        {
            var t = new Passes();
            var m = t.Graph.DefineFunction<Map<Node, long>>("m");
            t.Root.Assume(m.Of().Is(t.M));

            t.Merge();

            Assert.Equal(Map.Of((t.Root.Find(t.K1), 2L)), t.Root.Get(m.Of()));
        }

        [Fact]
        public void RowsOfMapsMeetAsThePassesMakeThem()
        {
            // f(M) = 100 and f({#3: 2}) = 200 meet when M is {(Num 1): 2}: egglog panics, and the batch refuses it.
            var conflict = new Passes();
            conflict.Root.Assume(conflict.F.Of(conflict.M).Is(100L), conflict.F.Of(Map.Of((conflict.K2, 2L))).Is(200L));
            Assert.Throws<FactConflictException>(conflict.Merge);
            Assert.False(conflict.Root.IsFaulted);

            // f(M) = 100 and f({#4: 3}) = 200 never meet, since M keeps 2: egglog keeps both rows.
            var apart = new Passes();
            apart.Root.Assume(apart.F.Of(apart.M).Is(100L), apart.F.Of(Map.Of((apart.K3, 3L))).Is(200L));
            apart.Merge();
            Assert.Equal(2, apart.Root.CountRows(apart.F));
        }

        [Fact]
        public void AMapCollapsesPassByPassInAChildAndItsRowsMeetAsThePassesMakeThem()
        {
            // Probes map-passes.egg and map-passes-conflict.egg, which put the merge of Passes in a push, one
            // action as a rule's head and, in the first, also two commands: egglog at 5e655cae makes M {(Num 1): 2}
            // there either way and keeps f(M) = 100 apart from f({#4: 3}) = 200, (print-size f) 2, and panics on f(M) =
            // 100 meeting f({#3: 2}) = 200, "Illegal merge attempted for function f"; after the pop M has its three
            // keys. Here the merge is the child's batch, whose check replays the passes of the child's rebuild over the
            // maps of its view.
            var t = new Passes();
            var m = t.Graph.DefineFunction<Map<Node, long>>("m");
            t.Root.Assume(m.Of().Is(t.M));
            t.Root.Assume(t.F.Of(t.M).Is(100L), t.F.Of(Map.Of((t.K3, 3L))).Is(200L));
            t.Root.Freeze();

            using (var child = t.Root.CreateChild())
            {
                t.MergeIn(child);
                Assert.Equal(Map.Of((child.Find(t.K1), 2L)), child.Get(m.Of()));
                Assert.Equal(2, child.CountRows(t.F));
                child.Engine.Context.CheckInvariants();
            }

            Assert.Equal(t.M, t.Root.Get(m.Of()));

            var conflict = new Passes();
            conflict.Root.Assume(conflict.F.Of(conflict.M).Is(100L), conflict.F.Of(Map.Of((conflict.K2, 2L))).Is(200L));
            conflict.Root.Freeze();
            using (var child = conflict.Root.CreateChild())
            {
                Assert.Throws<FactConflictException>(() => conflict.MergeIn(child));
                Assert.False(child.IsFaulted);
                Assert.False(child.AreEqual(conflict.A, conflict.B));
                Assert.Equal(2, child.CountRows(conflict.F));
            }
        }

        [Fact]
        public void AMergeCheckReadsCountsNotRepeats()
        {
            // A multiset of 2^40 of one class: the check of a merge reads its distinct elements, never its repeats.
            var t = new Theory();
            var g = t.Graph.DefineFunction<MultiSet<Node>, long>("g");
            t.Root.Assume(g.Of(Primitives.MultiSetSingle<Node>().Of(t.Num.Of(1L), 1L << 40)).Is(1L));

            t.Root.Union(t.Make(1L), t.Make(2L));

            Assert.True(t.Root.Check(Primitives.MultiSetCount<Node>().Of(Primitives.MultiSetSingle<Node>().Of(t.Num.Of(2L), 1L << 40), t.Num.Of(1L)).Is(1L << 40)));
            Assert.Equal(1, t.Root.CountRows(g));
        }

        [Fact]
        public void AConstantMapWhoseContainerKeysAreOneIsRefused()
        {
            // Which of two vector keys egglog's rebuild keeps depends on their containers' cells, which a map the host holds
            // from before a union no longer knows.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterMap<Vec<Node>, long>();
            var m = graph.DefineFunction<long, Map<Vec<Node>, long>>("m");
            var root = graph.Root;
            var (one, two) = (root.Make(num.Of(1L)), root.Make(num.Of(2L)));
            var before = Map.Of((Vec.Of(two), 20L), (Vec.Of(one), 10L));
            root.Assume(m.Of(1L).Is(before));

            root.Union(one, two);

            Assert.Equal(1, root.Get(m.Of(1L)).Count);
            var refused = Assert.Throws<ArgumentException>(() => root.Assume(m.Of(2L).Is(before)));
            Assert.Contains("one now", refused.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ClassesOfTwoGraphsAreTwoKeys()
        {
            var first = new Theory();
            var second = new Theory();
            var (a, b) = (first.Make(1L), second.Make(1L));
            var map = Map.Of((a, 1L), (b, 2L));
            Assert.Equal(2, map.Count);
            Assert.NotEqual(0, Map.Of((a, 1L)).CompareTo(Map.Of((b, 1L))));
            Assert.Equal(2, MultiSet.Of(a, b).Counts.Count());
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
        }

    }

}
