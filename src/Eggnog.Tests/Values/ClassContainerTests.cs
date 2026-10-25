using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Pairs and vectors of classes, egglog's eq-sort containers: rebuilt when their classes merge, their ids merged when
    /// their contents become equal, never unioned, and made only at the root; ports of egglog's tests at 5e655cae.
    /// </summary>
    public sealed class ClassContainerTests
    {

        [Fact]
        public void VectorsBecomeEqualWhenTheirClassesMerge()
        {
            // tests/container-rebuild.egg, the first program.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var of = Primitives.VecOf<Node>();
            var v1 = graph.DefineFunction<Vec<Node>>("v1");
            var v2 = graph.DefineFunction<Vec<Node>>("v2");
            var root = graph.Root;
            root.Assume(v1.Of().Is(of.Of(num.Of(1L), num.Of(2L))));
            root.Assume(v2.Of().Is(of.Of(num.Of(2L), num.Of(2L))));
            Assert.False(root.Check(v1.Of().Is(v2.Of())));

            root.Union(root.Make(num.Of(1L)), root.Make(num.Of(2L)));
            Assert.True(root.Check(v1.Of().Is(v2.Of())));

            var myVec = graph.DefineConstructor<Vec<Node>, Node>("MyVec");
            root.Make(myVec.Of(v1.Of()));
            Assert.True(root.Check(Fact.Exists(myVec.Of(v2.Of()))));
            Assert.True(root.Check(myVec.Of(v1.Of()).Is(myVec.Of(v2.Of()))));

            var v3 = graph.DefineFunction<Vec<Node>>("v3");
            root.Assume(v3.Of().Is(of.Of(num.Of(4L), num.Of(5L))));
            root.Union(root.Make(num.Of(4L)), root.Make(num.Of(6L)));
            root.Union(root.Make(num.Of(5L)), root.Make(num.Of(7L)));
            Assert.False(root.Check(myVec.Of(v3.Of()).Is(myVec.Of(of.Of(num.Of(6L), num.Of(7L))))));
            root.Make(myVec.Of(v3.Of()));
            Assert.True(root.Check(myVec.Of(v3.Of()).Is(myVec.Of(of.Of(num.Of(6L), num.Of(7L))))));
        }

        [Fact]
        public void APairOfAClassReadsItsClassCanonical()
        {
            // tests/pair.egg, lines 19-29.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterPair<Node, long>();
            var mp = graph.DefineFunction<Pair<Node, long>>("mp");
            var root = graph.Root;
            var a = root.Make(num.Of(1L));
            var b = root.Make(num.Of(2L));
            root.Assume(mp.Of().Is(Primitives.PairOf<Node, long>().Of(a, 42L)));
            Assert.True(root.Check(Primitives.PairFirst<Node, long>().Of(mp.Of()).Is(a)));
            Assert.True(root.Check(Primitives.PairSecond<Node, long>().Of(mp.Of()).Is(42L)));

            root.Union(a, b);
            Assert.True(root.Check(Primitives.PairFirst<Node, long>().Of(mp.Of()).Is(b)));
        }

        [Fact]
        public void AVectorRebuiltInPlaceIsMatchedAgain()
        {
            // tests/container-fail.egg (egglog #831): l(vec-of(w(k(b)))) rewrites to b once w(k(b)) is k(b).
            foreach (var rebuilt in new[] { true, false })
            {
                var graph = new EGraph();
                graph.DefineSort<Node>("E");
                graph.Values.RegisterVec<Node>();
                var b = graph.DefineConstructor<Node>("b");
                var k = graph.DefineConstructor<Node, Node>("k");
                var w = graph.DefineConstructor<Node, Node>("w");
                var l = graph.DefineConstructor<Vec<Node>, Node>("l");
                var rules = graph.DefineRuleset("rules");
                rules.DefineRule("unwrap", r =>
                {
                    var (e, x) = r.Vars<Node, Node>("e", "x");
                    r.When(e.Is(w.Of(x)));
                    r.Then(e.Is(x));
                });
                rules.DefineRule("open", r =>
                {
                    var (e, x, y) = r.Vars<Node, Node, Node>("e", "x", "y");
                    var v = r.Var<Vec<Node>>("v");
                    r.When(e.Is(l.Of(v)), y.Is(k.Of(x)), v.Is(Primitives.VecOf<Node>().Of(y)));
                    r.Then(e.Is(x));
                });
                var root = graph.Root;
                var top = rebuilt
                    ? root.Make(l.Of(Primitives.VecOf<Node>().Of(w.Of(k.Of(b.Of())))))
                    : root.Make(l.Of(Primitives.VecOf<Node>().Of(k.Of(b.Of()))));

                root.Run(Schedule.Saturate(Schedule.Run(rules)));

                Assert.True(root.AreEqual(top, root.Make(b.Of())));
            }
        }

        [Fact]
        public void ANestedVectorRebuiltInPlaceIsMatchedAgain()
        {
            // tests/nested-container-dirty-propagation.egg (egglog #888): the inner vector of p(vec-of(vec-of(w(b))))
            // rebuilds, and the outer one keeps its id.
            var graph = new EGraph();
            graph.DefineSort<Node>("E");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<Vec<Node>>();
            var b = graph.DefineConstructor<Node>("b");
            var w = graph.DefineConstructor<Node, Node>("w");
            var p = graph.DefineConstructor<Vec<Vec<Node>>, Node>("p");
            var inner = Primitives.VecOf<Node>();
            var outer = Primitives.VecOf<Vec<Node>>();
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("unwrap", r =>
            {
                var (e, x) = r.Vars<Node, Node>("e", "x");
                r.When(e.Is(w.Of(x)));
                r.Then(e.Is(x));
            });
            rules.DefineRule("open", r =>
            {
                var (e, x) = r.Vars<Node, Node>("e", "x");
                var v = r.Var<Vec<Vec<Node>>>("v");
                r.When(e.Is(p.Of(v)), x.Is(b.Of()), v.Is(outer.Of(inner.Of(x))));
                r.Then(e.Is(b.Of()));
            });
            var root = graph.Root;
            var nested = root.Make(p.Of(outer.Of(inner.Of(w.Of(b.Of())))));

            root.Run(Schedule.Saturate(Schedule.Run(rules)));

            Assert.True(root.AreEqual(nested, root.Make(b.Of())));
        }

        [Fact]
        public void ContainersOfClassesSerializeAndTakeIdsAsEgglogs()
        {
            // (datatype Math (Num i64) (Add Math Math)) (sort MV (Vec Math)) (sort PV (Pair Math MV))
            // (constructor L (MV) Math) (constructor P (PV) Math)
            // (L (vec-of (Num 1) (Num 1))) (L (vec-of (Num 2))) (P (pair (Num 2) (vec-of (Num 1) (Num 2))))
            // (union (Num 1) (Num 2)) (run 1), serialized with --to-json; the sort names are the library's.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.DefineConstructor<Node, Node, Node>("Add");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterPair<Node, Vec<Node>>();
            var l = graph.DefineConstructor<Vec<Node>, Node>("L");
            var p = graph.DefineConstructor<Pair<Node, Vec<Node>>, Node>("P");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            root.Make(l.Of(of.Of(num.Of(1L), num.Of(1L))));
            root.Make(l.Of(of.Of(num.Of(2L))));
            root.Make(p.Of(Primitives.PairOf<Node, Vec<Node>>().Of(num.Of(2L), of.Of(num.Of(1L), num.Of(2L)))));
            root.Union(root.Make(num.Of(1L)), root.Make(num.Of(2L)));

            var serialized = root.Serialize();
            var nodes = serialized.Nodes.Select(n => $"{n.Id} {n.Op} [{string.Join(", ", n.Children)}] {n.Class}");
            Assert.Equal(
                new[]
                {
                    "primitive-i64-1 1 [] i64-1",
                    "function-0-Num Num [primitive-i64-1] Math-0",
                    "primitive-i64-2 2 [] i64-2",
                    "function-1-Num Num [primitive-i64-2] Math-0",
                    "primitive-Vec<Math>-1 vec-of [function-1-Num, function-0-Num] Vec<Math>-1",
                    "function-0-L L [primitive-Vec<Math>-1] Math-2",
                    "primitive-Vec<Math>-4 vec-of [function-1-Num] Vec<Math>-4",
                    "function-1-L L [primitive-Vec<Math>-4] Math-5",
                    "primitive-Pair<Math, Vec<Math>>-7 Pair<Math, Vec<Math>> [function-0-Num, primitive-Vec<Math>-1] Pair<Math, Vec<Math>>-7",
                    "function-0-P P [primitive-Pair<Math, Vec<Math>>-7] Math-8",
                },
                nodes);
        }

        [Fact]
        public void AConstructorOverAVectorOfClassesExtractsItsElements()
        {
            // (extract (L (vec-of (Num 1) (Add (Num 2) (Num 3))))) prints the term, before and after (union (Num 1) (Num 4)).
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var add = graph.DefineConstructor<Node, Node, Node>("Add");
            graph.Values.RegisterVec<Node>();
            var l = graph.DefineConstructor<Vec<Node>, Node>("L");
            var root = graph.Root;
            var x = root.Make(l.Of(Primitives.VecOf<Node>().Of(num.Of(1L), add.Of(num.Of(2L), num.Of(3L)))));
            var term = root.Extract(x);
            Assert.Equal("(L (vec-of (Num 1) (Add (Num 2) (Num 3))))", term.ToString());
            Assert.Equal<ulong>(1 + 2 + 5, term.Cost);

            // The argument is the vector of the classes; each extracts as its own term.
            var elements = term.Value<Vec<Node>>(0);
            Assert.Equal(2, elements.Count);
            Assert.Equal("(Add (Num 2) (Num 3))", root.Extract(elements[1]).ToString());

            root.Union(root.Make(num.Of(1L)), root.Make(num.Of(4L)));
            Assert.Equal("(L (vec-of (Num 1) (Add (Num 2) (Num 3))))", root.Extract(x).ToString());
        }

        [Fact]
        public void AMergeThatMakesTwoKeysOneVectorConflicts()
        {
            // (function g (MV) i64 :no-merge) (set (g (vec-of (Num 1))) 10) (set (g (vec-of (Num 2))) 20)
            // (union (Num 1) (Num 2)) panics in egglog: "Illegal merge attempted for function g". The batch refuses it.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var g = graph.DefineFunction<Vec<Node>, long>("g");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            root.Assume(g.Of(of.Of(num.Of(1L))).Is(10L), g.Of(of.Of(num.Of(2L))).Is(20L));
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));

            Assert.Throws<FactConflictException>(() => root.Union(one, two));
            Assert.False(root.AreEqual(one, two));
            Assert.Equal(2, root.CountRows(g));

            // Equal values meet without conflict, and the rows become one.
            root.Assume(g.Of(of.Of(num.Of(3L))).Is(30L), g.Of(of.Of(num.Of(4L))).Is(30L));
            root.Union(root.Make(num.Of(3L)), root.Make(num.Of(4L)));
            Assert.Equal(3, root.CountRows(g));
            Assert.True(root.Check(g.Of(of.Of(num.Of(4L))).Is(30L)));
        }

        [Fact]
        public void RowsThatMeetWithDifferentVectorsOfClassesConflict()
        {
            // (function f (Math) MV :no-merge): a union of the keys makes the rows meet; their vectors are of classes the
            // union leaves apart, so the merge panics in egglog, and the batch refuses it.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<Node, Vec<Node>>("f");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            root.Assume(f.Of(num.Of(1L)).Is(of.Of(num.Of(5L))), f.Of(num.Of(2L)).Is(of.Of(num.Of(6L))));
            Assert.Throws<FactConflictException>(() => root.Union(root.Make(num.Of(1L)), root.Make(num.Of(2L))));

            // Vectors whose classes the same batch merges are one when the rows meet.
            root.Assume(f.Of(num.Of(3L)).Is(of.Of(num.Of(7L))), f.Of(num.Of(4L)).Is(of.Of(num.Of(8L))));
            root.Write(w =>
            {
                w.Union(w.Make(num.Of(7L)), w.Make(num.Of(8L)));
                w.Union(w.Make(num.Of(3L)), w.Make(num.Of(4L)));
            });
            Assert.True(root.Check(f.Of(num.Of(3L)).Is(f.Of(num.Of(4L)))));
        }

        [Fact]
        public void AQueryBindsAVectorOfClassesCanonical()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<long, Vec<Node>>("f");
            var root = graph.Root;
            root.Assume(f.Of(1L).Is(Primitives.VecOf<Node>().Of(num.Of(1L))), f.Of(2L).Is(Primitives.VecOf<Node>().Of(num.Of(2L))));
            root.Union(root.Make(num.Of(1L)), root.Make(num.Of(2L)));

            Var<Vec<Node>>? v = null;
            var query = graph.DefineQuery("same vector", q =>
            {
                v = q.Var<Vec<Node>>("v");
                q.When(f.Of(1L).Is(v), f.Of(2L).Is(v));
            });
            var found = new List<Vec<Node>>();
            root.ForEachMatch(query, match =>
            {
                found.Add(match.Get(v!));
                return true;
            });
            Assert.Equal(new[] { Vec.Of(root.Make(num.Of(1L))) }, found);
        }

        [Fact]
        public void AConstantVectorMayHoldAClassItsBatchMade()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<Vec<Node>>("f");
            var root = graph.Root;
            root.Write(w =>
            {
                var made = w.Make(num.Of(9L));
                w.Assume(f.Of().Is(Vec.Of(made, made)));
            });
            var nine = root.Make(num.Of(9L));
            Assert.Equal(Vec.Of(nine, nine), root.Get(f.Of()));
        }

        [Fact]
        public void AWriteOfAVectorOfClassesAfterAMergeInOneBatchSeesTheMerge()
        {
            // (union one two) (set (f) (vec-of two)): the vector holds the merged class, as egglog's next command makes it.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<Vec<Node>>("f");
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));
            root.Write(w =>
            {
                w.Union(one, two);
                w.Assume(f.Of().Is(Primitives.VecOf<Node>().Of(two)));
            });
            Assert.True(root.AreEqual(one, two));
            Assert.Equal(Vec.Of(root.Find(two)), root.Get(f.Of()));
        }

        [Fact]
        public void AChildReadsVectorsOfClassesButMakesNone()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<Vec<Node>>("f");
            var g = graph.DefineFunction<long, Vec<Node>>("g");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            root.Assume(f.Of().Is(of.Of(num.Of(1L))));
            var one = root.Make(num.Of(1L));
            root.Freeze();
            var child = root.CreateChild();

            Assert.Equal(Vec.Of(one), child.Get(f.Of()));
            Assert.True(child.Check(f.Of().Is(of.Of(num.Of(1L)))));
            Assert.True(child.Check(f.Of().Is(Vec.Of(one))));

            // The root holds (vec-of (Num 1)), which the child may write; it makes no new vector.
            child.Assume(g.Of(1L).Is(of.Of(num.Of(1L))));
            Assert.True(child.Check(g.Of(1L).Is(f.Of())));
            var made = Assert.Throws<RuleEvaluationException>(() => child.Assume(g.Of(2L).Is(of.Of(num.Of(1L), num.Of(1L)))));
            Assert.Contains("only the root interns", made.Message, StringComparison.Ordinal);
            Assert.False(child.IsFaulted);

            // And a container type of classes registers only while the root is writable.
            var frozen = Assert.Throws<InvalidOperationException>(() => graph.Values.RegisterPair<Node, Node>());
            Assert.Contains("root is frozen", frozen.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AChildMergesClassesAVectorHoldsWhenTheyStayCanonical()
        {
            // The library lets a child merge a class a root container holds only when the class stays canonical
            // (decisions row 9).
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<Vec<Node>>("f");
            var root = graph.Root;
            root.Make(num.Of(1L));
            root.Assume(f.Of().Is(Primitives.VecOf<Node>().Of(num.Of(2L))));
            var two = root.Make(num.Of(2L));
            var three = root.Make(num.Of(3L));
            root.Freeze();

            // A merge that keeps the held class canonical needs no rebuild of the vector.
            var free = root.CreateChild();
            free.Union(two, three);
            Assert.True(free.AreEqual(two, three));
            Assert.Equal(Vec.Of(two), free.Get(f.Of()));
            Assert.False(free.IsFaulted);
        }

        [Fact]
        public void RulesReadAndBuildVectorsOfClasses()
        {
            // (rule ((= e (L v)) (= x (vec-get v 0))) ((union e x) (set (h e) (vec-push v e))))
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var l = graph.DefineConstructor<Vec<Node>, Node>("L");
            var h = graph.DefineFunction<Node, Vec<Node>>("h");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("first", r =>
            {
                var (e, x) = r.Vars<Node, Node>("e", "x");
                var v = r.Var<Vec<Node>>("v");
                r.When(e.Is(l.Of(v)), x.Is(Primitives.VecGet<Node>().Of(v, 0L)));
                r.Then(e.Is(x), h.Of(e).Is(Primitives.VecPush<Node>().Of(v, e)));
            });
            var root = graph.Root;
            var top = root.Make(l.Of(Primitives.VecOf<Node>().Of(num.Of(1L), num.Of(2L))));

            root.Run(Schedule.Run(rules));

            var one = root.Make(num.Of(1L));
            Assert.True(root.AreEqual(top, one));
            Assert.True(root.Check(h.Of(top).Is(Primitives.VecOf<Node>().Of(num.Of(1L), num.Of(2L), num.Of(1L)))));
            Assert.Equal(Vec.Of(one, root.Make(num.Of(2L)), one), root.Get(h.Of(one)));
        }

        [Fact]
        public void AStaleClassInAConstantVectorIsCanonical()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<long, Vec<Node>>("f");
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));
            root.Assume(f.Of(1L).Is(Vec.Of(one)));
            root.Union(one, two);

            // Whichever of the two the union kept, a vector written or read with the other is the same vector.
            root.Assume(f.Of(2L).Is(Vec.Of(two)));
            Assert.True(root.Check(f.Of(1L).Is(f.Of(2L))));
            Assert.True(root.Check(f.Of(1L).Is(Vec.Of(two)), f.Of(2L).Is(Vec.Of(one))));
            Assert.False(root.Check(f.Of(1L).Is(Vec.Of(one, one))));
        }

        [Fact]
        public void AVectorOfVectorsMergesAPassAfterTheVectorsItHolds()
        {
            // (function f (Math) VVM :no-merge) (set (f (Num 1)) (vec-of (vec-of (Num 3)))) (set (f (Num 2)) (vec-of (vec-of (Num 4))))
            // then one action of (union (Num 3) (Num 4)) (union (Num 1) (Num 2)), here a primitive that makes both unions:
            // egglog's rebuild merges the outer vectors a pass after the rows meet, and panics; the batch refuses it. Two
            // commands are fine, in one batch or two.
            foreach (var oneAction in new[] { true, false })
            {
                var graph = new EGraph();
                graph.DefineSort<Node>("Math");
                var num = graph.DefineConstructor<long, Node>("Num");
                graph.Values.RegisterVec<Node>();
                graph.Values.RegisterVec<Vec<Node>>();
                var f = graph.DefineFunction<Node, Vec<Vec<Node>>>("f");
                var inner = Primitives.VecOf<Node>();
                var outer = Primitives.VecOf<Vec<Node>>();
                var root = graph.Root;
                root.Assume(f.Of(num.Of(1L)).Is(outer.Of(inner.Of(num.Of(3L)))), f.Of(num.Of(2L)).Is(outer.Of(inner.Of(num.Of(4L)))));
                var (one, two, three, four) = (root.Make(num.Of(1L)), root.Make(num.Of(2L)), root.Make(num.Of(3L)), root.Make(num.Of(4L)));
                if (oneAction)
                {
                    var o = graph.DefineFunction<long, long>("o");
                    var both = Primitive.CreateWrite("both", [], (ITableWriter tables, ReadOnlySpan<object> _, out long result) =>
                    {
                        tables.Union(three, four);
                        tables.Union(one, two);
                        result = 0;
                        return true;
                    });
                    Assert.Throws<FactConflictException>(() => root.Assume(o.Of(0L).Is(both.Of())));
                    Assert.False(root.IsFaulted);
                    Assert.False(root.AreEqual(three, four));
                }
                else
                {
                    root.Write(w =>
                    {
                        w.Union(three, four);
                        w.Union(one, two);
                    });
                    Assert.True(root.Check(f.Of(num.Of(1L)).Is(f.Of(num.Of(2L)))));
                }
            }
        }

        [Fact]
        public void KeysOfVectorsOfVectorsMeetAPassLate()
        {
            // (function f (VVM) Math :no-merge) (set (f (vec-of (vec-of (Num 1)))) (K (Num 1))) and the same of 2, then
            // (union (Num 1) (Num 2)): K's rows meet first, so when the keys meet their classes are one. egglog accepts it.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var k = graph.DefineConstructor<Node, Node>("K");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<Vec<Node>>();
            var f = graph.DefineFunction<Vec<Vec<Node>>, Node>("f");
            var inner = Primitives.VecOf<Node>();
            var outer = Primitives.VecOf<Vec<Node>>();
            var root = graph.Root;
            root.Assume(
                f.Of(outer.Of(inner.Of(num.Of(1L)))).Is(k.Of(num.Of(1L))),
                f.Of(outer.Of(inner.Of(num.Of(2L)))).Is(k.Of(num.Of(2L))));

            root.Union(root.Make(num.Of(1L)), root.Make(num.Of(2L)));

            Assert.True(root.AreEqual(root.Make(k.Of(num.Of(1L))), root.Make(k.Of(num.Of(2L)))));
            Assert.Equal(1, root.CountRows(f));
        }

        [Fact]
        public void AFrozenRootsQueryInternsNoVector()
        {
            // A read of a frozen root, which may run beside its children, interns nothing: a vector it computes that the
            // root does not hold matches nothing.
            var graph = new EGraph();
            graph.DefineSort<Node>("E");
            graph.Values.RegisterVec<Node>();
            var b = graph.DefineConstructor<Node>("b");
            var c = graph.DefineConstructor<Node>("c");
            var k = graph.DefineConstructor<Node, Node>("k");
            var l = graph.DefineConstructor<Vec<Node>, Node>("l");
            var root = graph.Root;
            root.Make(l.Of(Primitives.VecOf<Node>().Of(k.Of(b.Of()))));
            var kb = root.Make(k.Of(b.Of()));
            var kc = root.Make(k.Of(c.Of()));
            root.Freeze();
            var child = root.CreateChild();
            child.Union(kb, kc);

            var query = graph.DefineQuery("open", q =>
            {
                var (e, y) = (q.Var<Node>("e"), q.Var<Node>("y"));
                var v = q.Var<Vec<Node>>("v");
                q.When(e.Is(l.Of(v)), y.Is(k.Of(q.Var<Node>("x"))), v.Is(Primitives.VecOf<Node>().Of(y)));
            });
            var before = graph.Values.InternedCount;
            Assert.Equal(1, root.Count(query));
            Assert.Equal(before, graph.Values.InternedCount);
            Assert.True(child.AreEqual(kb, kc));
            Assert.False(child.IsFaulted);
        }

        [Fact]
        public void AChildMatchesVectorsItCannotMakeAsNothing()
        {
            // The container-fail rule in a child: the vector of k(c) is none the root holds, so it matches no row.
            var graph = new EGraph();
            graph.DefineSort<Node>("E");
            graph.Values.RegisterVec<Node>();
            var b = graph.DefineConstructor<Node>("b");
            var c = graph.DefineConstructor<Node>("c");
            var k = graph.DefineConstructor<Node, Node>("k");
            var l = graph.DefineConstructor<Vec<Node>, Node>("l");
            var g = graph.DefineFunction<Vec<Node>, long>("g");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("open", r =>
            {
                var (e, x, y) = r.Vars<Node, Node, Node>("e", "x", "y");
                var v = r.Var<Vec<Node>>("v");
                r.When(e.Is(l.Of(v)), y.Is(k.Of(x)), v.Is(Primitives.VecOf<Node>().Of(y)));
                r.Then(e.Is(x));
            });
            var looked = new List<bool>();
            var lookup = graph.DefineRuleset("lookup");
            lookup.DefineRule("look", r =>
            {
                var y = r.Var<Node>("y");
                r.When(y.Is(k.Of(r.Var<Node>("x"))));
                r.ThenCallFull(ctx => looked.Add(ctx.TryLookup<long>(g, [Vec.Of(ctx.Get(y), ctx.Get(y))], out _)));
            });
            var root = graph.Root;
            var top = root.Make(l.Of(Primitives.VecOf<Node>().Of(k.Of(b.Of()))));
            root.Make(k.Of(c.Of()));
            root.Freeze();
            var child = root.CreateChild();

            child.Run(Schedule.Run(rules));
            child.Run(Schedule.Run(lookup));

            Assert.False(child.IsFaulted);
            Assert.True(child.AreEqual(top, child.Make(b.Of())));
            Assert.Equal(new[] { false, false }, looked);
        }

        [Fact]
        public void AChildsMergeOfAClassAVectorHoldsIsRefusedBeforeItIsWritten()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<Vec<Node>>("f");
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            root.Assume(f.Of().Is(Primitives.VecOf<Node>().Of(num.Of(2L))));
            var two = root.Make(num.Of(2L));
            root.Freeze();
            var child = root.CreateChild();

            var refused = Assert.Throws<InvalidOperationException>(() => child.Union(one, two));
            Assert.Contains("container of classes holds", refused.Message, StringComparison.Ordinal);
            Assert.False(child.IsFaulted);
            Assert.False(child.AreEqual(one, two));
            Assert.Equal(Vec.Of(two), child.Get(f.Of()));
        }

        [Fact]
        public void AVectorAPrimitiveReturnsHoldsCanonicalClasses()
        {
            // A primitive that kept a class the host held, stale since, returns a vector of it canonical, as a class it
            // returns is.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<long, Vec<Node>>("f");
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));
            root.Assume(f.Of(1L).Is(Vec.Of(one)));
            root.Union(one, two);
            var stale = root.Find(one).Equals(one) ? two : one;
            var kept = Primitive.Create<long, Vec<Node>>("kept", _ => Vec.Of(stale));

            root.Assume(f.Of(1L).Is(kept.Of(0L)));
            Assert.True(root.Check(f.Of(1L).Is(kept.Of(0L))));
        }

        [Fact]
        public void UnequalVectorsStayUnequal()
        {
            // tests/repro-vec-unequal.egg.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<long>();
            var v1 = graph.DefineFunction<Vec<Node>>("v1");
            var v2 = graph.DefineFunction<Vec<Node>>("v2");
            var v3 = graph.DefineFunction<Vec<long>>("v3");
            var v4 = graph.DefineFunction<Vec<long>>("v4");
            var root = graph.Root;
            root.Assume(
                v1.Of().Is(Primitives.VecOf<Node>().Of(num.Of(1L), num.Of(2L))),
                v2.Of().Is(Primitives.VecOf<Node>().Of(num.Of(2L), num.Of(2L))),
                v3.Of().Is(Primitives.VecOf<long>().Of(1L, 2L)),
                v4.Of().Is(Primitives.VecOf<long>().Of(2L, 2L)));
            Assert.False(root.Check(v1.Of().Is(v2.Of())));
            Assert.False(root.Check(v3.Of().Is(v4.Of())));
        }

        [Fact]
        public void AVectorAChildRebuildsInPlaceIsMatchedAgainThere()
        {
            // Probe ov1-root-row-child-dirty.egg, which egglog at 5e655cae runs clean: (rewrite (w x) x) and (rewrite
            // (l (vec-of (k x))) x) over (let $r (l (vec-of (w (k (b)))))); (push), saturate, (check (= $r (b))),
            // (pop), (fail (check (= $r (b)))), saturate, (check (= $r (b))). In the child the union of w(k(b)) and
            // k(b) rebuilds the root's vector in place, in the child's container layers, and re-stamps the root's row l
            // of it there, so the second rule matches it in the child.
            var graph = new EGraph();
            graph.DefineSort<Node>("E");
            graph.Values.RegisterVec<Node>();
            var b = graph.DefineConstructor<Node>("b");
            var k = graph.DefineConstructor<Node, Node>("k");
            var w = graph.DefineConstructor<Node, Node>("w");
            var l = graph.DefineConstructor<Vec<Node>, Node>("l");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("unwrap", r =>
            {
                var (e, x) = r.Vars<Node, Node>("e", "x");
                r.When(e.Is(w.Of(x)));
                r.Then(e.Is(x));
            });
            rules.DefineRule("open", r =>
            {
                var (e, x, y) = r.Vars<Node, Node, Node>("e", "x", "y");
                var v = r.Var<Vec<Node>>("v");
                r.When(e.Is(l.Of(v)), y.Is(k.Of(x)), v.Is(Primitives.VecOf<Node>().Of(y)));
                r.Then(e.Is(x));
            });
            var root = graph.Root;
            var top = root.Make(l.Of(Primitives.VecOf<Node>().Of(w.Of(k.Of(b.Of())))));

            using (var child = root.CreateChild())
            {
                child.Run(Schedule.Saturate(Schedule.Run(rules)));
                Assert.False(child.IsFaulted);
                Assert.True(child.AreEqual(top, child.Make(b.Of())));
            }

            Assert.False(root.AreEqual(top, root.Make(b.Of())));
            root.Run(Schedule.Saturate(Schedule.Run(rules)));
            Assert.True(root.AreEqual(top, root.Make(b.Of())));
        }

        [Fact]
        public void AChildsRuleOverAVectorItRebuildsInPlaceFiresPastTheInheritedWatermark()
        {
            // Probe dirty-parent-row.egg, its first block, which egglog at 5e655cae runs clean: (rewrite (l (vec-of (k
            // x))) x) saturated at the root over (let $r (l (vec-of (w (k (b)))))) leaves $r apart from (b); in a push
            // the union of w(k(b)) and k(b), here a rule's, rebuilds the vector in place, and the saturation there,
            // which takes up from the root's progress, matches the root's row l of the vector again, re-stamped in the
            // child.
            var graph = new EGraph();
            graph.DefineSort<Node>("E");
            graph.Values.RegisterVec<Node>();
            var b = graph.DefineConstructor<Node>("b");
            var k = graph.DefineConstructor<Node, Node>("k");
            var w = graph.DefineConstructor<Node, Node>("w");
            var l = graph.DefineConstructor<Vec<Node>, Node>("l");
            var go = graph.DefineRelation("go");
            var open = graph.DefineRuleset("open");
            open.DefineRule("open", r =>
            {
                var (e, x, y) = r.Vars<Node, Node, Node>("e", "x", "y");
                var v = r.Var<Vec<Node>>("v");
                r.When(e.Is(l.Of(v)), y.Is(k.Of(x)), v.Is(Primitives.VecOf<Node>().Of(y)));
                r.Then(e.Is(x));
            });
            var merge = graph.DefineRuleset("merge");
            merge.DefineRule("merge", r =>
            {
                r.When(go.Of());
                r.Then(w.Of(k.Of(b.Of())).Is(k.Of(b.Of())));
            });
            var root = graph.Root;
            var top = root.Make(l.Of(Primitives.VecOf<Node>().Of(w.Of(k.Of(b.Of())))));
            root.Run(Schedule.Saturate(Schedule.Run(open)));
            Assert.False(root.AreEqual(top, root.Make(b.Of())));

            using (var child = root.CreateChild())
            {
                child.Assume(go.Of());
                child.Run(Schedule.Run(merge));
                child.Run(Schedule.Saturate(Schedule.Run(open)));
                Assert.False(child.IsFaulted);
                Assert.True(child.AreEqual(top, child.Make(b.Of())));
            }

            Assert.False(root.AreEqual(top, root.Make(b.Of())));
        }

        [Fact]
        public void ANestedVectorAChildRebuildsIsMatchedThereAndCollapsesItsHolders()
        {
            // Probe judge-nested-closure.egg, which egglog at 5e655cae runs clean. The inner vector of
            // p(vec-of(vec-of(w(b)))), made before the push, rebuilds in place there, the outer vector only by closure,
            // and a rule over the root's row p must fire in the child; then two pairs of nested vectors that collide in
            // the child collapse their holders' rows. The unions are a rule's, in the child.
            var graph = new EGraph();
            graph.DefineSort<Node>("E");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<Vec<Node>>();
            var b = graph.DefineConstructor<Node>("b");
            var w = graph.DefineConstructor<Node, Node>("w");
            var p = graph.DefineConstructor<Vec<Vec<Node>>, Node>("p");
            var a = graph.DefineConstructor<Node>("A");
            var bb = graph.DefineConstructor<Node>("B");
            var h = graph.DefineConstructor<Vec<Vec<Node>>, Node>("h");
            var innerOf = graph.DefineFunction<Vec<Node>>("inner");
            var va = graph.DefineFunction<Vec<Node>>("va");
            var vb = graph.DefineFunction<Vec<Node>>("vb");
            var oa = graph.DefineFunction<Vec<Vec<Node>>>("oa");
            var ob = graph.DefineFunction<Vec<Vec<Node>>>("ob");
            var ok = graph.DefineRelation("ok");
            var go = graph.DefineRelation("go");
            var pairs = graph.DefineRelation("pairs");
            var inner = Primitives.VecOf<Node>();
            var outer = Primitives.VecOf<Vec<Node>>();
            var fire = graph.DefineRuleset("fire");
            fire.DefineRule("fire", r =>
            {
                var (e, x) = r.Vars<Node, Node>("e", "x");
                var v = r.Var<Vec<Vec<Node>>>("v");
                r.When(e.Is(p.Of(v)), x.Is(b.Of()), v.Is(outer.Of(inner.Of(x))));
                r.Then(ok.Of());
            });
            var merge = graph.DefineRuleset("merge");
            merge.DefineRule("unwrap", r =>
            {
                r.When(go.Of());
                r.Then(w.Of(b.Of()).Is(b.Of()));
            });
            merge.DefineRule("pair", r =>
            {
                r.When(pairs.Of());
                r.Then(a.Of().Is(bb.Of()));
            });
            var root = graph.Root;
            root.Assume(innerOf.Of().Is(inner.Of(w.Of(b.Of()))));
            root.Make(p.Of(outer.Of(innerOf.Of())));
            root.Run(Schedule.Saturate(Schedule.Run(fire)));
            Assert.False(root.Check(ok.Of()));

            using (var child = root.CreateChild())
            {
                child.Assume(go.Of());
                child.Run(Schedule.Run(merge));
                child.Run(Schedule.Saturate(Schedule.Run(fire)));
                Assert.True(child.Check(ok.Of()));
                Assert.True(child.Check(innerOf.Of().Is(inner.Of(b.Of()))));
            }

            Assert.False(root.Check(ok.Of()));

            root.Assume(va.Of().Is(inner.Of(a.Of())), vb.Of().Is(inner.Of(bb.Of())));
            root.Assume(oa.Of().Is(outer.Of(va.Of())), ob.Of().Is(outer.Of(vb.Of())));
            var ha = root.Make(h.Of(oa.Of()));
            var hb = root.Make(h.Of(ob.Of()));
            root.Freeze();
            using (var child = root.CreateChild())
            {
                child.Assume(pairs.Of());
                child.Run(Schedule.Run(merge));
                Assert.True(child.Check(va.Of().Is(vb.Of())));
                Assert.True(child.Check(oa.Of().Is(ob.Of())));
                Assert.True(child.AreEqual(ha, hb));
            }

            Assert.False(root.AreEqual(ha, hb));
            Assert.False(root.Check(va.Of().Is(vb.Of())));
        }

        [Fact]
        public void EveryContainerOfClassesRebuildsInAChildAsInEgglogsPush()
        {
            // Probe push-sorts-novc.egg, which egglog at 5e655cae runs clean: a set, a map, a multiset and two vectors
            // of classes made at the root, then in a push (union (B) (C)) and (union (A) (B)), here a rule's. The set
            // shrinks, the map's keys collide and the later key's value stays, the multiset's counts add, the vectors
            // become one and so do their holders' rows, and the rows keyed by the set and the map are found by their
            // new contents. After the pop the root has them as they were.
            var graph = new EGraph();
            graph.DefineSort<Node>("M");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var c = graph.DefineConstructor<Node>("C");
            graph.Values.RegisterSet<Node>();
            graph.Values.RegisterMap<Node, long>();
            graph.Values.RegisterMultiSet<Node>();
            graph.Values.RegisterVec<Node>();
            var fs = graph.DefineFunction<Set<Node>, long>("fs", ValueLattice.Max<long>());
            var fm = graph.DefineFunction<Map<Node, long>, long>("fm", ValueLattice.Max<long>());
            var holder = graph.DefineConstructor<Vec<Node>, Node>("W");
            var s = graph.DefineFunction<Set<Node>>("s");
            var m = graph.DefineFunction<Map<Node, long>>("m");
            var ms = graph.DefineFunction<MultiSet<Node>>("ms");
            var va = graph.DefineFunction<Vec<Node>>("va");
            var vb = graph.DefineFunction<Vec<Node>>("vb");
            var go = graph.DefineRelation("go");
            var merge = graph.DefineRuleset("merge");
            merge.DefineRule("merge", r =>
            {
                r.When(go.Of());
                r.Then(b.Of().Is(c.Of()), a.Of().Is(b.Of()));
            });
            var insert = Primitives.MapInsert<Node, long>();
            var empty = Primitives.MapEmpty<Node, long>();
            var root = graph.Root;
            root.Assume(s.Of().Is(Primitives.SetOf<Node>().Of(a.Of(), b.Of())));
            root.Assume(m.Of().Is(insert.Of(insert.Of(empty.Of(), a.Of(), 10L), b.Of(), 20L)));
            root.Assume(ms.Of().Is(Primitives.MultiSetOf<Node>().Of(a.Of(), b.Of(), b.Of())));
            root.Assume(va.Of().Is(Primitives.VecOf<Node>().Of(a.Of())), vb.Of().Is(Primitives.VecOf<Node>().Of(b.Of())));
            root.Make(holder.Of(vb.Of()));
            root.Assume(fs.Of(s.Of()).Is(1L), fm.Of(m.Of()).Is(1L));
            root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Assume(go.Of());
                child.Run(Schedule.Run(merge));
                Assert.False(child.IsFaulted);
                Assert.True(child.Check(Primitives.Count<Node>().Of(s.Of()).Is(1L)));
                Assert.True(child.Check(Primitives.MapLength<Node, long>().Of(m.Of()).Is(1L)));
                Assert.True(child.Check(Primitives.MapGet<Node, long>().Of(m.Of(), a.Of()).Is(20L)));
                Assert.True(child.Check(Primitives.MultiSetCount<Node>().Of(ms.Of(), a.Of()).Is(3L)));
                Assert.True(child.Check(va.Of().Is(vb.Of())));
                Assert.True(child.Check(holder.Of(va.Of()).Is(holder.Of(vb.Of()))));
                Assert.True(child.Check(fs.Of(Primitives.SetOf<Node>().Of(a.Of())).Is(1L)));
                Assert.True(child.Check(fm.Of(insert.Of(empty.Of(), a.Of(), 20L)).Is(1L)));
                var one = child.Find(child.Make(a.Of()));
                Assert.Equal(Set.Of(one), child.Get(s.Of()));
                Assert.Equal(Map.Of((one, 20L)), child.Get(m.Of()));
                Assert.Equal(MultiSet.Of(one, one, one), child.Get(ms.Of()));
            }

            Assert.True(root.Check(Primitives.Count<Node>().Of(s.Of()).Is(2L)));
            Assert.False(root.Check(va.Of().Is(vb.Of())));
            Assert.False(root.Check(holder.Of(va.Of()).Is(holder.Of(vb.Of()))));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AChildsFailedRunKeepsTheVectorsItsUnionMerged(bool frozen)
        {
            // Probe fail-push.egg: (datatype Math (A) (B)) (sort MV (Vec Math)) (function f (MV) i64 :merge (max old new))
            // (set (f (vec-of (A))) 1) (set (f (vec-of (B))) 2) (rule ((= x (A))) ((union (A) (B)) (panic "stop"))
            // :ruleset r), then (push) (fail (run r 1)). egglog at 5e655cae returns the panic before it rebuilds, so
            // (check (= (A) (B))) fails in the push (the egglog engine map's defect 22); the port rebuilds what a failed
            // run staged, as a root running the same program here does. So in the child, which the failure does not
            // fault, the union stands, its container pass merges the two vectors and the rows keyed by them, and a
            // grandchild live across the run catches up with it. The child goes on: a rule over (vec-of (A)) fires, and
            // the failing ruleset, whose progress counted, does not fail again. The rule fails here by a division by
            // zero after its union.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var lit = graph.DefineConstructor<long, Node>("Lit");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<Vec<Node>, long>("f", ValueLattice.Max<long>());
            var g = graph.DefineFunction<Vec<Node>, long>("g", ValueLattice.Max<long>());
            var w = graph.DefineConstructor<Vec<Node>, Node>("W");
            var go = graph.DefineRelation("go");
            var merge = graph.DefineRuleset("merge");
            merge.DefineRule("u", r =>
            {
                r.When(go.Of());
                r.Then(a.Of().Is(b.Of()), Fact.Exists(lit.Of(Primitives.Divide.Of(10L, 0L))));
            });
            var follow = graph.DefineRuleset("follow");
            follow.DefineRule("follow", r =>
            {
                var v = r.Var<Vec<Node>>("v");
                var n = r.Var<long>("n");
                r.When(v.Is(Primitives.VecOf<Node>().Of(a.Of())), n.Is(f.Of(v)));
                r.Then(g.Of(v).Is(n));
            });
            var va = Primitives.VecOf<Node>().Of(a.Of());
            var vb = Primitives.VecOf<Node>().Of(b.Of());
            var root = graph.Root;
            root.Assume(f.Of(va).Is(1L), f.Of(vb).Is(2L));
            root.Make(w.Of(vb));
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                using var grandchild = child.CreateChild();
                child.Assume(go.Of());
                Assert.Throws<RuleEvaluationException>(() => child.Run(Schedule.Run(merge)));
                Assert.False(child.IsFaulted);
                Assert.True(child.Engine.Context.IsClean);
                child.Engine.Context.CheckInvariants();
                Assert.True(child.AreEqual(child.Make(a.Of()), child.Make(b.Of())));
                Assert.True(child.Check(va.Is(vb)));
                Assert.Equal(2L, child.Get(f.Of(va)));
                Assert.True(child.Check(w.Of(va).Is(w.Of(vb))));
                Assert.True(grandchild.Check(va.Is(vb)));
                Assert.Equal(2L, grandchild.Get(f.Of(va)));
                grandchild.Engine.Context.CheckInvariants();

                child.Run(Schedule.Run(follow));
                Assert.Equal(2L, child.Get(g.Of(va)));
                child.Run(Schedule.Run(merge));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.Check(va.Is(vb)));
            if (frozen)
                return;

            root.Assume(go.Of());
            Assert.Throws<RuleEvaluationException>(() => root.Run(Schedule.Run(merge)));
            Assert.True(root.Check(va.Is(vb)));
            Assert.Equal(2L, root.Get(f.Of(va)));
            Assert.True(root.Check(w.Of(va).Is(w.Of(vb))));
            root.Run(Schedule.Run(follow));
            Assert.Equal(2L, root.Get(g.Of(va)));
            root.Run(Schedule.Run(merge));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AChildsRunWhoseRebuildFailsKeepsTheVectorsItsContainerPassMerged(bool frozen)
        {
            // A run whose rule is clean and whose rebuild fails: in the child the rule's union of (A) and (B) makes the
            // container pass merge (vec-of (A)) with (vec-of (B)), then (vec-of (vec-of (A))) with (vec-of (vec-of (B)))
            // a pass later, and the rows of f keyed by the two outer vectors meet in the tables' pass, where f's lattice
            // throws. The failure is recorded, as a panic is, and the run throws it once its rebuild ends, so the child is
            // not faulted and the vectors stay merged, f keeping the row its failed merge left, as a root running the
            // same program does. The child goes on, the ruleset no longer failing.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<Vec<Node>>();
            var boom = new ValueLattice<long>("boom", static (x, y) => (x, y) is (1L, 2L) or (2L, 1L) ? throw new InvalidOperationException("boom") : Math.Max(x, y));
            var f = graph.DefineFunction<Vec<Vec<Node>>, long>("f", boom);
            var go = graph.DefineRelation("go");
            var merge = graph.DefineRuleset("merge");
            merge.DefineRule("u", r =>
            {
                r.When(go.Of());
                r.Then(a.Of().Is(b.Of()));
            });
            var inner = Primitives.VecOf<Node>();
            var outer = Primitives.VecOf<Vec<Node>>();
            var va = inner.Of(a.Of());
            var vb = inner.Of(b.Of());
            var oa = outer.Of(va);
            var ob = outer.Of(vb);
            var root = graph.Root;
            root.Assume(f.Of(oa).Is(1L), f.Of(ob).Is(2L));
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Assume(go.Of());
                Assert.Throws<RuleEvaluationException>(() => child.Run(Schedule.Run(merge)));
                Assert.False(child.IsFaulted);
                Assert.True(child.Engine.Context.IsClean);
                child.Engine.Context.CheckInvariants();
                Assert.True(child.Check(va.Is(vb)));
                Assert.True(child.Check(oa.Is(ob)));
                Assert.Equal(1L, child.Get(f.Of(oa)));

                child.Run(Schedule.Run(merge));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.Check(oa.Is(ob)));
            if (frozen)
                return;

            root.Assume(go.Of());
            Assert.Throws<RuleEvaluationException>(() => root.Run(Schedule.Run(merge)));
            Assert.True(root.Check(oa.Is(ob)));
            Assert.Equal(1L, root.Get(f.Of(oa)));
            root.Run(Schedule.Run(merge));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AFailedFlushInAChildsBatchKeepsTheVectorsItMergedAndTheBatchGoesOn(bool frozen)
        {
            // A host batch in the child unions (A) and (B), which makes (K (A)) and (K (B)) one by congruence, and its
            // container pass then (vec-of (K (A))) and (vec-of (K (B))), so the rows of f keyed by them meet and f's
            // lattice throws. The batch's next call flushes first and throws the recorded failure; the batch goes on,
            // its later write landing on the merged vector, and the child is not faulted, as a root's batch running the
            // same calls is not.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var k = graph.DefineConstructor<Node, Node>("K");
            graph.Values.RegisterVec<Node>();
            var boom = new ValueLattice<long>("boom", static (x, y) => (x, y) is (1L, 2L) or (2L, 1L) ? throw new InvalidOperationException("boom") : Math.Max(x, y));
            var f = graph.DefineFunction<Vec<Node>, long>("f", boom);
            var h = graph.DefineFunction<Vec<Node>, long>("h", ValueLattice.Max<long>());
            var va = Primitives.VecOf<Node>().Of(k.Of(a.Of()));
            var vb = Primitives.VecOf<Node>().Of(k.Of(b.Of()));
            var root = graph.Root;
            root.Assume(f.Of(va).Is(1L), f.Of(vb).Is(2L));
            if (frozen)
                root.Freeze();

            void Batch(Scope scope)
            {
                var na = scope.Make(a.Of());
                var nb = scope.Make(b.Of());
                scope.Write(batch =>
                {
                    batch.Union(na, nb);
                    Assert.Throws<RuleEvaluationException>(() => batch.Assume(h.Of(va).Is(7L)));
                    batch.Assume(h.Of(vb).Is(9L));
                });
            }

            using (var child = root.CreateChild())
            {
                Batch(child);
                Assert.False(child.IsFaulted);
                Assert.True(child.Engine.Context.IsClean);
                child.Engine.Context.CheckInvariants();
                Assert.True(child.Check(k.Of(a.Of()).Is(k.Of(b.Of()))));
                Assert.True(child.Check(va.Is(vb)));
                Assert.Equal(1L, child.Get(f.Of(va)));
                Assert.Equal(9L, child.Get(h.Of(va)));

                child.Assume(h.Of(va).Is(11L));
                Assert.Equal(11L, child.Get(h.Of(vb)));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.Check(va.Is(vb)));
            if (frozen)
                return;

            Batch(root);
            Assert.True(root.Check(va.Is(vb)));
            Assert.Equal(1L, root.Get(f.Of(va)));
            Assert.Equal(9L, root.Get(h.Of(va)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AChildCatchesUpWithVectorsItsWritableRootMakesAcrossFailedRuns(bool replay)
        {
            // Catch-up across failed runs, under a writable root that makes vectors while a child and a
            // grandchild live. First the root makes (vec-of (A)) and (vec-of (B)) before the child's run, whose rule
            // unions (A) and (B) and then fails: the child had no displacement when it caught up, so it consumed the new
            // vectors unread, and the failed run's rebuild, which the failure does not stop, claims (vec-of (B)) for the
            // union's loser and merges it with (vec-of (A)). Then the root makes (vec-of (A) (A)) and (vec-of (B) (B)),
            // the latter holding the child's loser: the child's next run catches up first, claiming it, so the two
            // merge and f's rows meet, and that run fails too. Neither failure faults the child, and the grandchild
            // sees the same. egglog has no writable root under a live push, so the expected values are those of a
            // fresh root running the same commands in the same order (replay), as a root does here: there
            // (vec-of (B) (B)) is (vec-of (A) (A)) once (A) and (B) are one, and f's rows meet at 5.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var lit = graph.DefineConstructor<long, Node>("Lit");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<Vec<Node>, long>("f", ValueLattice.Max<long>());
            var go = graph.DefineRelation("go");
            var stop = graph.DefineRelation("stop");
            var merge = graph.DefineRuleset("merge");
            merge.DefineRule("u", r =>
            {
                r.When(go.Of());
                r.Then(a.Of().Is(b.Of()), Fact.Exists(lit.Of(Primitives.Divide.Of(10L, 0L))));
            });
            var fail = graph.DefineRuleset("fail");
            fail.DefineRule("fail", r =>
            {
                r.When(stop.Of());
                r.Then(Fact.Exists(lit.Of(Primitives.Divide.Of(20L, 0L))));
            });
            var va = Primitives.VecOf<Node>().Of(a.Of());
            var vb = Primitives.VecOf<Node>().Of(b.Of());
            var vaa = Primitives.VecOf<Node>().Of(a.Of(), a.Of());
            var vbb = Primitives.VecOf<Node>().Of(b.Of(), b.Of());
            var root = graph.Root;
            var child = replay ? root : root.CreateChild();
            var grandchild = replay ? root : child.CreateChild();

            root.Assume(f.Of(va).Is(1L), f.Of(vb).Is(2L));
            child.Assume(go.Of());
            Assert.Throws<RuleEvaluationException>(() => child.Run(Schedule.Run(merge)));
            root.Assume(f.Of(vaa).Is(3L), f.Of(vbb).Is(5L));
            child.Assume(stop.Of());
            Assert.Throws<RuleEvaluationException>(() => child.Run(Schedule.Run(fail)));

            Assert.False(child.IsFaulted);
            Assert.True(child.Engine.Context.IsClean);
            child.Engine.Context.CheckInvariants();
            Assert.True(child.Check(va.Is(vb)));
            Assert.Equal(2L, child.Get(f.Of(va)));
            Assert.True(child.Check(vaa.Is(vbb)));
            Assert.Equal(5L, child.Get(f.Of(vaa)));
            Assert.True(grandchild.Check(vaa.Is(vbb)));
            Assert.Equal(5L, grandchild.Get(f.Of(vbb)));
            grandchild.Engine.Context.CheckInvariants();
            if (replay)
                return;

            grandchild.Dispose();
            child.Dispose();
            Assert.False(root.Check(va.Is(vb)));
            Assert.False(root.Check(vaa.Is(vbb)));
            Assert.Equal(3L, root.Get(f.Of(vaa)));
        }

        [Fact]
        public void ContainersOfClassesAreNeverUnionedAndHaveNoRuleConstants()
        {
            // (union $v1 $v2) is refused: "Cannot union values of sort MV".
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var v1 = graph.DefineFunction<Vec<Node>>("v1");
            var v2 = graph.DefineFunction<Vec<Node>>("v2");
            var root = graph.Root;
            root.Assume(v1.Of().Is(Primitives.VecOf<Node>().Of(num.Of(1L))), v2.Of().Is(Primitives.VecOf<Node>().Of(num.Of(3L))));
            Assert.Throws<ArgumentException>(() => root.Assume(Primitives.VecOf<Node>().Of(num.Of(1L)).Is(Primitives.VecOf<Node>().Of(num.Of(3L)))));

            var n = root.Make(num.Of(1L));
            var rules = graph.DefineRuleset("rules");
            Assert.ThrowsAny<Exception>(() => rules.DefineRule("constant", r =>
            {
                var x = r.Var<Vec<Node>>("x");
                r.When(x.Is(v1.Of()));
                r.Then(v2.Of().Is(Vec.Of(n)));
            }));
        }

    }

}
