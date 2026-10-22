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
        public void AWriteOfAVectorOfClassesAfterAMergeInOneBatchIsRefused()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<Vec<Node>>("f");
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));
            Assert.Throws<InvalidOperationException>(() => root.Write(w =>
            {
                w.Union(one, two);
                w.Assume(f.Of().Is(Primitives.VecOf<Node>().Of(two)));
            }));
            Assert.False(root.AreEqual(one, two));
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
            // A child never rebuilds containers, so it may merge a class a root container holds only when the class stays
            // canonical (decisions row 9).
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
            // then one action of (union (Num 3) (Num 4)) (union (Num 1) (Num 2)): egglog's rebuild merges the outer vectors
            // a pass after the rows meet, and panics; the batch refuses it. Two commands are fine.
            foreach (var oneBatch in new[] { true, false })
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
                if (oneBatch)
                {
                    Assert.Throws<FactConflictException>(() => root.Write(w =>
                    {
                        w.Union(three, four);
                        w.Union(one, two);
                    }));
                    Assert.False(root.IsFaulted);
                    Assert.False(root.AreEqual(three, four));
                }
                else
                {
                    root.Union(three, four);
                    root.Union(one, two);
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
