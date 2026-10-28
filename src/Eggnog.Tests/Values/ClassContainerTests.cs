using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Pairs and vectors of classes, egglog's eq-sort containers: rebuilt when their classes merge, at the root and in
    /// a child, which rebuilds its own copies as egglog's push does, their ids merged when their contents become equal,
    /// never unioned, and made at a writable root and in a writable child, which holds what it makes in its own
    /// containers, as egglog's push holds them in its copy, until it is disposed; ports of egglog's tests at 5e655cae.
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
        public void APairOfAClassAChildMergesReadsItsClassCanonicalThere()
        {
            // Probe pair-child.egg, which egglog at 5e655cae runs clean: (Num 1), (set (mp) (pair (Num 2) 42)), and
            // g of (pair (Num 1) 42) 7 and of (mp) 3 under max; in a push (union (Num 1) (Num 2)) rebuilds (mp) to (pair
            // (Num 1) 42), which merges with the other pair, so (check (= (g (mp)) 7)) and g has one row; after the pop
            // (g (mp)) is 3 and g has two.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterPair<Node, long>();
            var mp = graph.DefineFunction<Pair<Node, long>>("mp");
            var g = graph.DefineFunction<Pair<Node, long>, long>("g", ValueLattice.Max<long>());
            var pair = Primitives.PairOf<Node, long>();
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            root.Assume(mp.Of().Is(pair.Of(num.Of(2L), 42L)));
            root.Assume(g.Of(pair.Of(num.Of(1L), 42L)).Is(7L), g.Of(mp.Of()).Is(3L));
            var two = root.Make(num.Of(2L));
            root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Union(one, two);
                Assert.True(child.Check(Primitives.PairFirst<Node, long>().Of(mp.Of()).Is(num.Of(1L))));
                Assert.True(child.Check(mp.Of().Is(pair.Of(num.Of(1L), 42L))));
                Assert.Equal(new Pair<Node, long>(one, 42L), child.Get(mp.Of()));
                Assert.Equal(7L, child.Get(g.Of(mp.Of())));
                Assert.Equal(1, child.CountRows(g));
                child.Engine.Context.CheckInvariants();
            }

            Assert.Equal(3L, root.Get(g.Of(mp.Of())));
            Assert.Equal(2, root.CountRows(g));
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RowsWhosePairsAMergeMakesOneMeetThoughAnotherTypeOfTheirLayoutNamesOne(bool child)
        {
            // Probe same-layout-meet.egg, which egglog at 5e655cae runs clean: two pair sorts of one layout,
            // (set (f (pair (Num 0) 1)) (C)), (set (f (pair (Num 1) 1)) (D)) and (KP (pair (Num 1) 1)) of the other sort;
            // (union (Num 0) (Num 1)) fails, since the container pass makes the pairs of f's keys one and f's rows meet
            // with two classes, "Illegal merge attempted for function f". Here KP is keyed by a pair of a class and an
            // i32, which holds the cells of the pair of a class and an i64 that keys f's second row, so the two are one
            // container, and the check's replay of the passes merges it with the other pair as the engine does, whichever
            // type names it first: the batch refuses the union before anything is written, at the root and in a child.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var c = graph.DefineConstructor<Node>("C");
            var d = graph.DefineConstructor<Node>("D");
            var pairs = graph.Values.RegisterPair<Node, long>();
            var narrowPairs = graph.Values.RegisterPair<Node, int>();
            var f = graph.DefineFunction<Pair<Node, long>, Node>("f");
            var kp = graph.DefineConstructor<Pair<Node, int>, Node>("KP");
            var pair = Primitives.PairOf<Node, long>();
            var root = graph.Root;
            var zero = root.Make(num.Of(0L));
            var one = root.Make(num.Of(1L));
            root.Assume(f.Of(pair.Of(num.Of(0L), 1L)).Is(c.Of()), f.Of(pair.Of(num.Of(1L), 1L)).Is(d.Of()));
            root.Make(kp.Of(Primitives.PairOf<Node, int>().Of(num.Of(1L), 1)));
            Assert.True(pairs.TryEncodeExisting(new Pair<Node, long>(one, 1L), root.ContainerView, out var keyed));
            Assert.True(narrowPairs.TryEncodeExisting(new Pair<Node, int>(one, 1), root.ContainerView, out var made));
            Assert.Equal(keyed, made);
            if (child)
                root.Freeze();

            var scope = child ? root.CreateChild() : root;
            var conflict = Assert.Throws<FactConflictException>(() => scope.Union(zero, one));
            Assert.Contains("Function 'f'", conflict.Message, StringComparison.Ordinal);
            Assert.False(scope.IsFaulted);
            Assert.False(scope.AreEqual(zero, one));
            Assert.Equal(2, scope.CountRows(f));
            Assert.Equal(1, scope.CountRows(kp));
            scope.Engine.Context.CheckInvariants();
            if (child == false)
                return;

            scope.Dispose();
            Assert.Equal(2, root.CountRows(f));
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

        /// <summary>
        /// A child of a frozen root reads the vectors of classes the root holds, writes one, and makes new ones, as
        /// egglog's push interns in its copy: in its own container layers, under ids local to it, which the root and a
        /// sibling never see. The expectation is egglog's at 5e655cae running probe child-makes-vector.egg, which exits
        /// clean: (set (f) (vec-of (Num 1))); in a push (g 1) is (f), (g 2) is (vec-of (Num 1) (Num 1)) of length 2 and
        /// (g 3) is (vec-of (Num 2)), of a class the push made; after the pop g has neither (g 2) nor (g 3).
        /// </summary>
        [Fact]
        public void AChildReadsAndMakesVectorsOfClasses()
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
            var interned = graph.Values.InternedCount;

            using (var child = root.CreateChild())
            {
                Assert.Equal(Vec.Of(one), child.Get(f.Of()));
                Assert.True(child.Check(f.Of().Is(of.Of(num.Of(1L)))));
                Assert.True(child.Check(f.Of().Is(Vec.Of(one))));

                // The root holds (vec-of (Num 1)), which the child writes; the others it makes.
                child.Assume(g.Of(1L).Is(of.Of(num.Of(1L))));
                Assert.True(child.Check(g.Of(1L).Is(f.Of())));
                child.Assume(g.Of(2L).Is(of.Of(num.Of(1L), num.Of(1L))));
                Assert.True(child.Check(g.Of(2L).Is(of.Of(num.Of(1L), num.Of(1L)))));
                Assert.Equal(Vec.Of(one, one), child.Get(g.Of(2L)));
                Assert.True(child.Check(Primitives.VecLength<Node>().Of(g.Of(2L)).Is(2L)));
                child.Assume(g.Of(3L).Is(of.Of(num.Of(2L))));
                var two = child.Make(num.Of(2L));
                Assert.Equal(Vec.Of(two), child.Get(g.Of(3L)));
                Assert.False(child.Check(g.Of(3L).Is(g.Of(2L))));
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();

                // The new vectors live in the child's own containers, not in the graph's pool.
                Assert.Equal(interned, graph.Values.InternedCount);
                using var sibling = root.CreateChild();
                Assert.False(sibling.TryGet(g.Of(2L), out _));
                Assert.False(sibling.TryGet(g.Of(3L), out _));
            }

            Assert.False(root.TryGet(g.Of(2L), out _));
            Assert.False(root.TryGet(g.Of(3L), out _));
            Assert.Equal(interned, graph.Values.InternedCount);
            root.Engine.Context.CheckInvariants();
        }

        /// <summary>
        /// A pair type of classes registers while the root is frozen and a child of it lives, as egglog declares
        /// <c>(sort MathPair (Pair Math Math))</c> inside a <c>(push)</c>: the pairs' environment, which held the pairs of
        /// values the root and the child made, comes to hold ids, and the child's container pass walks it from then on.
        /// The expectation is egglog's at 5e655cae running probe late-pair.egg, which exits clean: (sort IntPair
        /// (Pair i64 i64)), (set (q) (pair 1 2)), (Num 1) and (Num 2); in a push, (sort MathPair (Pair Math Math)) and
        /// (union (Num 1) (Num 2)), after which (q) is still (pair 1 2) and the two are equal; after the pop they are not.
        /// The child also makes a pair of values after the type came to hold ids, which interns anywhere.
        /// </summary>
        [Fact]
        public void APairTypeOfClassesRegistersWhileTheRootIsFrozen()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterPair<long, long>();
            var q = graph.DefineFunction<Pair<long, long>>("q");
            var r = graph.DefineFunction<Pair<long, long>>("r");
            var root = graph.Root;
            root.Assume(q.Of().Is(Primitives.PairOf<long, long>().Of(1L, 2L)));
            var one = root.Make(num.Of(1L));
            var two = root.Make(num.Of(2L));
            root.Freeze();
            var containers = graph.Engine.ContainerValues;
            Assert.False(containers.GetEnvironment<Relations.PairContainer>().HoldsIds);

            using (var child = root.CreateChild())
            {
                child.Assume(r.Of().Is(Primitives.PairOf<long, long>().Of(3L, 4L)));
                var pairs = graph.Values.RegisterPair<Node, Node>();
                Assert.Equal("Pair<Math, Math>", pairs.Name);
                Assert.True(containers.GetEnvironment<Relations.PairContainer>().HoldsIds);
                Assert.True(containers.HasEqContainers);

                child.Union(one, two);
                Assert.True(child.AreEqual(one, two));
                Assert.Equal(new Pair<long, long>(1L, 2L), child.Get(q.Of()));
                Assert.Equal(new Pair<long, long>(3L, 4L), child.Get(r.Of()));
                child.Assume(r.Of().Is(Primitives.PairOf<long, long>().Of(3L, 4L)));
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.AreEqual(one, two));
            Assert.Equal(new Pair<long, long>(1L, 2L), root.Get(q.Of()));
            containers.CheckInvariants();
            root.Engine.Context.CheckInvariants();
        }

        /// <summary>
        /// A pair type of classes registers from no rule's filter, in a child of a frozen root as anywhere else: a
        /// registration is a call into the graph, which a primitive, filter, lattice, cost function or scheduler may not
        /// make, so it throws there, the rule still fires, and the pairs' environment holds no ids until a registration
        /// outside the run.
        /// </summary>
        [Fact]
        public void APairTypeOfClassesRegistersFromNoFilterOfAChildsRule()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            graph.Values.RegisterPair<long, long>();
            var seed = graph.DefineRelation<long>("seed");
            var done = graph.DefineRelation<long>("done");
            var rules = graph.DefineRuleset("rules");
            var refusals = new List<Exception>();
            rules.DefineRule("register", r =>
            {
                var x = r.Var<long>("x");
                r.When(seed.Of(x));
                r.Where(x, _ =>
                {
                    refusals.Add(Assert.Throws<InvalidOperationException>(() => graph.Values.RegisterPair<Node, Node>()));
                    return true;
                });
                r.Then(done.Of(x));
            });
            var root = graph.Root;
            root.Freeze();
            var containers = graph.Engine.ContainerValues;

            using (var child = root.CreateChild())
            {
                child.Assume(seed.Of(1L));
                child.Run(Schedule.Run(rules));
                Assert.True(child.Check(done.Of(1L)));
            }

            var refusal = Assert.Single(refusals);
            Assert.Equal("Not allowed from a primitive, lattice, filter, cost function or scheduler", refusal.Message);
            Assert.False(containers.GetEnvironment<Relations.PairContainer>().HoldsIds);
            graph.Values.RegisterPair<Node, Node>();
            Assert.True(containers.GetEnvironment<Relations.PairContainer>().HoldsIds);
        }

        [Fact]
        public void AChildMergesClassesAVectorHoldsWhenTheyStayCanonical()
        {
            // (Num 1), (set (f) (vec-of (Num 2))) and (Num 3), then in a push (union (Num 2) (Num 3)): (Num 2) has the
            // smaller id and stays canonical, so the vector holds a class the merge does not displace, and the child's
            // container pass leaves it as the root holds it, as egglog's push leaves its copy.
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

            var free = root.CreateChild();
            free.Union(two, three);
            Assert.True(free.AreEqual(two, three));
            Assert.Equal(Vec.Of(two), free.Get(f.Of()));
            Assert.True(free.Check(f.Of().Is(Primitives.VecOf<Node>().Of(num.Of(3L)))));
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
        public void AChildMatchesVectorsNoRowHoldsAsNothing()
        {
            // The container-fail rule in a child, probe child-matches-new-vector.egg, which egglog at 5e655cae runs
            // clean: $top = (l (vec-of (k (b)))) and (k (c)) at the root; in a push the rule's (vec-of (k x)) of x =
            // (c) makes a vector no l row holds, which the push interns and matches nothing, so $top becomes (b) only.
            // Here the child interns it in its own containers, and a lookup of g, which reads, interns nothing and
            // finds nothing.
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
            var kc = root.Make(k.Of(c.Of()));
            root.Freeze();
            var child = root.CreateChild();
            var vectors = graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>();
            var layers = child.Engine.Context.Context.ContainerLayers!;
            Assert.Null(layers.LayerAt(vectors.TypeId));

            var interned = graph.Values.InternedCount;
            child.Run(Schedule.Run(rules));

            // The rule's (vec-of (k (c))) is the one vector the child's own containers hold, under an id local to it.
            var layer = (Relations.ContainerLayer<Relations.VecContainer>)layers.LayerAt(vectors.TypeId)!;
            var entries = new List<Relations.ContainerEntry<Relations.VecContainer>>();
            var collector = new Relations.ContainerEntryCollector<Relations.VecContainer>(entries);
            layer.Own.ForEach(ref collector);
            var made = Assert.Single(entries);
            Assert.Equal(new Relations.VecContainer(true, [kc.Id.Cell]), made.Container);
            Assert.True(child.Engine.Context.Context.IsLocalId(made.Id));

            child.Run(Schedule.Run(lookup));

            Assert.False(child.IsFaulted);
            Assert.True(child.AreEqual(top, child.Make(b.Of())));
            Assert.False(child.AreEqual(top, child.Make(c.Of())));
            Assert.Equal(new[] { false, false }, looked);
            Assert.Equal(1, layer.OwnBase.Count);
            Assert.Equal(interned, graph.Values.InternedCount);
            child.Engine.Context.CheckInvariants();
        }

        [Fact]
        public void AChildsRulesAndCallbacksFindTheVectorsItsHostMergeRebuiltAndNoOthers()
        {
            // Probe child-rebuilt-matches.egg, which egglog at 5e655cae runs clean: (k (c)), $top = (l (vec-of (W)))
            // and (set (g (vec-of (W))) 7); in a push (union (W) (k (c))) displaces (W), which the vector holds, so the
            // container-fail rule's let (= v (vec-of y)) of y = (k (c)) joins the rebuilt vector and unions $top with
            // (c), and a rule reading (g (vec-of y)) finds 7 while one reading (g (vec-of y y)) finds nothing; after the
            // pop neither fires. Here the reads of g are a rule callback's lookups, which encode the vector through the
            // child's view, the rebuilt copy included, and find no row for a vector the view lacks.
            var graph = new EGraph();
            graph.DefineSort<Node>("E");
            graph.Values.RegisterVec<Node>();
            var c = graph.DefineConstructor<Node>("c");
            var k = graph.DefineConstructor<Node, Node>("k");
            var w = graph.DefineConstructor<Node>("W");
            var l = graph.DefineConstructor<Vec<Node>, Node>("l");
            var g = graph.DefineFunction<Vec<Node>, long>("g", ValueLattice.Max<long>());
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("open", r =>
            {
                var (e, x, y) = r.Vars<Node, Node, Node>("e", "x", "y");
                var v = r.Var<Vec<Node>>("v");
                r.When(e.Is(l.Of(v)), y.Is(k.Of(x)), v.Is(Primitives.VecOf<Node>().Of(y)));
                r.Then(e.Is(x));
            });
            var looked = new List<long?>();
            var look = graph.DefineRuleset("look");
            look.DefineRule("look", r =>
            {
                var y = r.Var<Node>("y");
                r.When(y.Is(k.Of(r.Var<Node>("x"))));
                r.ThenCallFull(ctx =>
                {
                    looked.Add(ctx.TryLookup<long>(g, [Vec.Of(ctx.Get(y))], out var one) ? one : null);
                    looked.Add(ctx.TryLookup<long>(g, [Vec.Of(ctx.Get(y), ctx.Get(y))], out var two) ? two : null);
                });
            });
            var root = graph.Root;
            var kc = root.Make(k.Of(c.Of()));
            var top = root.Make(l.Of(Primitives.VecOf<Node>().Of(w.Of())));
            root.Assume(g.Of(Primitives.VecOf<Node>().Of(w.Of())).Is(7L));
            var wc = root.Make(w.Of());
            root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Union(wc, kc);
                child.Run(rules);
                child.Run(look);
                Assert.False(child.IsFaulted);
                Assert.True(child.AreEqual(top, child.Make(c.Of())));
                Assert.Equal(new long?[] { 7L, null }, looked);
                child.Engine.Context.CheckInvariants();
            }

            looked.Clear();
            using (var child = root.CreateChild())
            {
                child.Run(rules);
                child.Run(look);
                Assert.False(child.AreEqual(top, child.Make(c.Of())));
                Assert.Equal(new long?[] { null, null }, looked);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AChildsRulesWriteTheVectorsItsHostMergeRebuiltAndNewOnes(bool callback)
        {
            // Probe child-rebuilt-writes.egg, which egglog at 5e655cae runs clean: (k (c)) and (set (hold) (vec-of
            // (W))); in a push (union (W) (k (c))) rebuilds the held vector to (vec-of (k (c))), and a rule setting
            // (g (vec-of y)) of y = (k (c)) writes g of that vector, one row; in another push a rule setting
            // (g (vec-of y y)) interns that new vector and writes g of it; after the pops g has no row. Here a rule
            // writes through its head's vec-of, or through a full callback's Set, either of which encodes the vector
            // through the child's view: the rebuilt copy writes, and a vector the view lacks the child interns in its
            // own containers and writes, as egglog's push does.
            var graph = new EGraph();
            graph.DefineSort<Node>("E");
            graph.Values.RegisterVec<Node>();
            var c = graph.DefineConstructor<Node>("c");
            var k = graph.DefineConstructor<Node, Node>("k");
            var w = graph.DefineConstructor<Node>("W");
            var hold = graph.DefineFunction<Vec<Node>>("hold");
            var g = graph.DefineFunction<Vec<Node>, long>("g", ValueLattice.Max<long>());
            var of = Primitives.VecOf<Node>();
            var rebuilt = Writes("rebuilt", twice: false);
            var fresh = Writes("fresh", twice: true);
            var root = graph.Root;
            var kc = root.Make(k.Of(c.Of()));
            root.Assume(hold.Of().Is(of.Of(w.Of())));
            var wc = root.Make(w.Of());
            root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Union(wc, kc);
                Assert.True(child.Check(hold.Of().Is(of.Of(k.Of(c.Of())))));
                child.Run(rebuilt);
                Assert.Equal(9L, child.Get(g.Of(hold.Of())));
                Assert.Equal(1, child.CountRows(g));
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            var interned = graph.Values.InternedCount;
            using (var child = root.CreateChild())
            {
                child.Union(wc, kc);
                child.Run(fresh);
                Assert.False(child.IsFaulted);
                Assert.Equal(1, child.CountRows(g));
                Assert.Equal(9L, child.Get(g.Of(of.Of(k.Of(c.Of()), k.Of(c.Of())))));
                Assert.False(child.TryGet(g.Of(hold.Of()), out _));
                Assert.True(child.Check(hold.Of().Is(of.Of(k.Of(c.Of())))));
                child.Engine.Context.CheckInvariants();
            }

            Assert.Equal(interned, graph.Values.InternedCount);

            Assert.Equal(0, root.CountRows(g));
            Assert.False(root.AreEqual(wc, kc));

            Ruleset Writes(string name, bool twice)
            {
                var set = graph.DefineRuleset(name);
                set.DefineRule(name, r =>
                {
                    var y = r.Var<Node>("y");
                    r.When(y.Is(k.Of(r.Var<Node>("x"))));
                    if (callback)
                        r.ThenCallFull(ctx => ctx.Set(g, [twice ? Vec.Of(ctx.Get(y), ctx.Get(y)) : Vec.Of(ctx.Get(y))], 9L));
                    else
                        r.Then(g.Of(twice ? of.Of(y, y) : of.Of(y)).Is(9L));
                });
                return set;
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AChildsMergeOfAClassAVectorHoldsRebuildsTheVectorThereOnly(bool frozen)
        {
            // Probe child-merge.egg, which egglog at 5e655cae runs clean: (Num 1), then (set (f) (vec-of (Num 2)));
            // in a push (union (Num 1) (Num 2)) displaces (Num 2), which the vector holds, and (check (= (f) (vec-of
            // (Num 1)))) holds there and in a push inside it; in a later push, a sibling here, and after the pops the
            // classes are apart and (f) is (vec-of (Num 2)). The child's host merge rebuilds the child's copy of the
            // vector; its sibling, the root and a child made after it is disposed see the root's.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var f = graph.DefineFunction<Vec<Node>>("f");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            root.Assume(f.Of().Is(of.Of(num.Of(2L))));
            var two = root.Make(num.Of(2L));
            if (frozen)
                root.Freeze();

            using var sibling = root.CreateChild();
            using (var child = root.CreateChild())
            {
                Assert.True(child.Union(one, two));
                Assert.False(child.IsFaulted);
                Assert.True(child.AreEqual(one, two));
                Assert.Equal(one, child.Find(two));
                Assert.Equal(Vec.Of(one), child.Get(f.Of()));
                Assert.True(child.Check(f.Of().Is(of.Of(num.Of(1L)))));
                Assert.True(child.Check(f.Of().Is(of.Of(num.Of(2L)))));
                child.Engine.Context.CheckInvariants();
                using (var grandchild = child.CreateChild())
                {
                    Assert.True(grandchild.Check(f.Of().Is(of.Of(num.Of(1L)))));
                    Assert.Equal(Vec.Of(one), grandchild.Get(f.Of()));
                }

                Assert.False(sibling.AreEqual(one, two));
                Assert.Equal(Vec.Of(two), sibling.Get(f.Of()));
                Assert.False(sibling.Check(f.Of().Is(of.Of(num.Of(1L)))));
            }

            Assert.False(root.AreEqual(one, two));
            Assert.Equal(Vec.Of(two), root.Get(f.Of()));
            Assert.False(root.Check(f.Of().Is(of.Of(num.Of(1L)))));
            using var later = root.CreateChild();
            Assert.False(later.AreEqual(one, two));
            Assert.Equal(Vec.Of(two), later.Get(f.Of()));
            Assert.False(later.Check(f.Of().Is(of.Of(num.Of(1L)))));
            root.Engine.Context.CheckInvariants();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AChildsMergeThatMakesTwoKeysOneVectorConflictsAsTheRootsDoes(bool replay)
        {
            // Probe child-conflict.egg: the program of AMergeThatMakesTwoKeysOneVectorConflicts in a push, where
            // egglog at 5e655cae panics on (union (Num 1) (Num 2)) as at its root, "Illegal merge attempted for function
            // g", which (fail ...) catches, and (union (Num 3) (Num 4)) then makes two rows of one value one. egglog keeps
            // the failed union's write in the push, where the library refuses the union before anything is written, in a
            // child as at the root, so the expected values are those of a fresh root running the same commands
            // (replay). The batch's check reads the vectors through the child's view, whose copies the child's
            // container pass rebuilds; the root keeps its four rows.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var g = graph.DefineFunction<Vec<Node>, long>("g");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            root.Assume(g.Of(of.Of(num.Of(1L))).Is(10L), g.Of(of.Of(num.Of(2L))).Is(20L));
            root.Assume(g.Of(of.Of(num.Of(3L))).Is(30L), g.Of(of.Of(num.Of(4L))).Is(30L));
            var (one, two, three, four) = (root.Make(num.Of(1L)), root.Make(num.Of(2L)), root.Make(num.Of(3L)), root.Make(num.Of(4L)));
            if (replay == false)
                root.Freeze();

            var scope = replay ? root : root.CreateChild();
            Assert.Throws<FactConflictException>(() => scope.Union(one, two));
            Assert.False(scope.IsFaulted);
            Assert.False(scope.AreEqual(one, two));
            Assert.Equal(4, scope.CountRows(g));

            scope.Union(three, four);
            Assert.Equal(3, scope.CountRows(g));
            Assert.True(scope.Check(g.Of(of.Of(num.Of(4L))).Is(30L)));
            Assert.True(scope.Check(of.Of(num.Of(3L)).Is(of.Of(num.Of(4L)))));
            Assert.Throws<FactConflictException>(() => scope.Union(one, two));
            scope.Engine.Context.CheckInvariants();
            if (replay)
                return;

            scope.Dispose();
            Assert.Equal(4, root.CountRows(g));
            Assert.False(root.AreEqual(three, four));
        }

        [Fact]
        public void AChildsLaterMergeIsCheckedAgainstTheVectorsItsEarlierMergeRebuilt()
        {
            // Probe child-second-merge.egg, which egglog at 5e655cae runs clean: g of (vec-of (Num 1) (Num 3)) 10 and
            // of (vec-of (Num 2) (Num 4)) 20, both :no-merge, and c of (vec-of (Num 5) (Num 7)) (Num 10) and of (vec-of
            // (Num 6) (Num 8)) (Num 20). In a push (union (Num 1) (Num 2)) rebuilds the second vector to (vec-of (Num 1)
            // (Num 4)), which g still reads as 20, and then (union (Num 3) (Num 4)) would make g's two rows meet, a panic
            // that (fail ...) catches; so for c, with (union (Num 5) (Num 6)) then (union (Num 7) (Num 8)), unless (union
            // (Num 10) (Num 20)) comes between. Here each later merge is a host merge of the child after the earlier one
            // flushed: its check reads the vector the child's container pass rebuilt, through the child's view, where the
            // root's holds the displaced class, and refuses the merge before anything is written.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var g = graph.DefineFunction<Vec<Node>, long>("g");
            var c = graph.DefineFunction<Vec<Node>, Node>("c");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            root.Assume(g.Of(of.Of(num.Of(1L), num.Of(3L))).Is(10L), g.Of(of.Of(num.Of(2L), num.Of(4L))).Is(20L));
            root.Assume(c.Of(of.Of(num.Of(5L), num.Of(7L))).Is(num.Of(10L)), c.Of(of.Of(num.Of(6L), num.Of(8L))).Is(num.Of(20L)));
            var n = new Dictionary<long, Node>();
            foreach (var i in new[] { 1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 10L, 20L })
                n[i] = root.Make(num.Of(i));
            root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Union(n[1], n[2]);
                Assert.Equal(20L, child.Get(g.Of(of.Of(num.Of(1L), num.Of(4L)))));
                Assert.Equal(2, child.CountRows(g));
                Assert.Throws<FactConflictException>(() => child.Union(n[3], n[4]));
                Assert.False(child.IsFaulted);
                Assert.False(child.AreEqual(n[3], n[4]));
                Assert.Equal(2, child.CountRows(g));
            }

            using (var child = root.CreateChild())
            {
                child.Union(n[5], n[6]);
                Assert.True(child.AreEqual(child.Get(c.Of(of.Of(num.Of(5L), num.Of(8L)))), n[20]));
                Assert.Throws<FactConflictException>(() => child.Union(n[7], n[8]));
                Assert.False(child.IsFaulted);
                Assert.False(child.AreEqual(n[7], n[8]));
            }

            using (var child = root.CreateChild())
            {
                child.Union(n[5], n[6]);
                child.Union(n[10], n[20]);
                child.Union(n[7], n[8]);
                Assert.True(child.AreEqual(child.Get(c.Of(of.Of(num.Of(5L), num.Of(7L)))), n[10]));
                Assert.Equal(1, child.CountRows(c));
                child.Engine.Context.CheckInvariants();
            }

            Assert.Equal(2, root.CountRows(g));
            Assert.Equal(2, root.CountRows(c));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AChildsVectorOfVectorsMergesAPassAfterTheVectorsItHolds(bool oneAction)
        {
            // Probes vv-one-action.egg and vv-two-commands.egg, the program of
            // AVectorOfVectorsMergesAPassAfterTheVectorsItHolds in a push: one action of (union (Num 3) (Num 4))
            // (union (Num 1) (Num 2)), a rule's in egglog, panics there, "Illegal merge attempted for function f", and
            // the two as two commands make f's rows one, (check (= (f (Num 1)) (f (Num 2)))); after the pop they are
            // apart. In the child the batch replays the passes of the child's rebuild over its view and refuses the one
            // action before anything is written.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<Vec<Node>>();
            var f = graph.DefineFunction<Node, Vec<Vec<Node>>>("f");
            var o = graph.DefineFunction<long, long>("o");
            var inner = Primitives.VecOf<Node>();
            var outer = Primitives.VecOf<Vec<Node>>();
            var root = graph.Root;
            root.Assume(f.Of(num.Of(1L)).Is(outer.Of(inner.Of(num.Of(3L)))), f.Of(num.Of(2L)).Is(outer.Of(inner.Of(num.Of(4L)))));
            var (one, two, three, four) = (root.Make(num.Of(1L)), root.Make(num.Of(2L)), root.Make(num.Of(3L)), root.Make(num.Of(4L)));
            var both = Primitive.CreateWrite("both", [], (ITableWriter tables, ReadOnlySpan<object> _, out long result) =>
            {
                tables.Union(three, four);
                tables.Union(one, two);
                result = 0;
                return true;
            });
            root.Freeze();

            using (var child = root.CreateChild())
            {
                if (oneAction)
                {
                    Assert.Throws<FactConflictException>(() => child.Assume(o.Of(0L).Is(both.Of())));
                    Assert.False(child.IsFaulted);
                    Assert.False(child.AreEqual(three, four));
                    Assert.False(child.Check(f.Of(num.Of(1L)).Is(f.Of(num.Of(2L)))));
                }
                else
                {
                    child.Write(w =>
                    {
                        w.Union(three, four);
                        w.Union(one, two);
                    });
                    Assert.True(child.Check(f.Of(num.Of(1L)).Is(f.Of(num.Of(2L)))));
                    Assert.True(child.Check(outer.Of(inner.Of(num.Of(3L))).Is(outer.Of(inner.Of(num.Of(4L))))));
                }

                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.Check(f.Of(num.Of(1L)).Is(f.Of(num.Of(2L)))));
            Assert.False(root.AreEqual(three, four));
        }

        [Fact]
        public void KeysOfVectorsOfVectorsMeetAPassLateInAChild()
        {
            // Probe vv-keys-late.egg, the program of KeysOfVectorsOfVectorsMeetAPassLate in a push, which egglog at
            // 5e655cae runs clean: (union (Num 1) (Num 2)) there makes K's rows one before f's keys meet, so (check (= (K
            // (Num 1)) (K (Num 2)))) holds and f has one row, (print-size f) 1; after the pop it has two. The child's
            // host merge is checked against the child's view as the root's is, and accepted.
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
            var (one, two) = (root.Make(num.Of(1L)), root.Make(num.Of(2L)));
            var (k1, k2) = (root.Make(k.Of(num.Of(1L))), root.Make(k.Of(num.Of(2L))));
            root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Union(one, two);
                Assert.True(child.AreEqual(k1, k2));
                Assert.Equal(1, child.CountRows(f));
                child.Engine.Context.CheckInvariants();
            }

            Assert.Equal(2, root.CountRows(f));
            Assert.False(root.AreEqual(k1, k2));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AChildsBatchReadsTheVectorsItsMergeRebuiltLaterInTheBatch(bool frozen)
        {
            // Probe batch-read.egg, which egglog at 5e655cae runs clean: (Num 1), (set (f) (vec-of (Num 2))), (let
            // $top (L (vec-of (Num 2)))) and (set (h (vec-of (Num 2))) 7) at the root; in a push (union (Num 1)
            // (Num 2)), then (fail (set (first) (vec-get (f) 5))), (set (first) (vec-get (f) 0)), (set (h (vec-of (Num
            // 1))) 9) and (set (g) (L (vec-of (Num 1)))), so that (first) is (Num 1), (h (f)) 9, (g) $top, and L and h
            // keep one row each; after the pop (h (f)) is 7 and (first) has no value. Here the commands are one batch
            // of the child: the facts after the union need it rebuilt, so the batch flushes first and the child's
            // container pass rebuilds its copy of the vector, which they then read and write by its new content; the
            // fact with no result fails alone.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var l = graph.DefineConstructor<Vec<Node>, Node>("L");
            var f = graph.DefineFunction<Vec<Node>>("f");
            var h = graph.DefineFunction<Vec<Node>, long>("h", ValueLattice.Max<long>());
            var first = graph.DefineFunction<Node>("first");
            var g = graph.DefineFunction<Node>("g");
            var of = Primitives.VecOf<Node>();
            var get = Primitives.VecGet<Node>();
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            root.Assume(f.Of().Is(of.Of(num.Of(2L))));
            var top = root.Make(l.Of(of.Of(num.Of(2L))));
            root.Assume(h.Of(of.Of(num.Of(2L))).Is(7L));
            var two = root.Make(num.Of(2L));
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Write(w =>
                {
                    w.Union(one, two);
                    Assert.Throws<RuleEvaluationException>(() => w.Assume(first.Of().Is(get.Of(f.Of(), 5L))));
                    w.Assume(first.Of().Is(get.Of(f.Of(), 0L)));
                    w.Assume(h.Of(of.Of(num.Of(1L))).Is(9L));
                    w.Assume(g.Of().Is(l.Of(of.Of(num.Of(1L)))));
                });
                Assert.False(child.IsFaulted);
                Assert.True(child.AreEqual(child.Get(first.Of()), one));
                Assert.Equal(9L, child.Get(h.Of(f.Of())));
                Assert.Equal(9L, child.Get(h.Of(of.Of(num.Of(2L)))));
                Assert.True(child.AreEqual(child.Get(g.Of()), top));
                Assert.Equal(1, child.CountRows(l));
                Assert.Equal(1, child.CountRows(h));
                child.Engine.Context.CheckInvariants();
            }

            Assert.Equal(7L, root.Get(h.Of(f.Of())));
            Assert.False(root.TryGet(first.Of(), out _));
            Assert.False(root.AreEqual(one, two));
            Assert.Equal(1, root.CountRows(l));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AChildsBatchWritesConstantVectorsItsMergeRebuiltLaterInTheBatch(bool frozen)
        {
            // Probe batch-constant.egg, which egglog at 5e655cae runs clean: $top = (L (vec-of (Num 2))), (set (h
            // (vec-of (Num 2))) 7) and (r (vec-of (Num 4))) at the root; in a push (union (Num 1) (Num 2)), then
            // $made = (L (vec-of (Num 1))), which is $top, (set (h (vec-of (Num 1))) 9), then (union (Num 3) (Num 4))
            // and (r (vec-of (Num 3))), so that r, L and h keep one row each. Here each push's commands are one batch of
            // the child, with constant vectors: the root holds none of their contents, which only the child's rebuild
            // makes, so the batch flushes before a fact that encodes a container of classes after a merge, as egglog's
            // next command runs after the last one's rebuild.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var l = graph.DefineConstructor<Vec<Node>, Node>("L");
            var r = graph.DefineRelation<Vec<Node>>("r");
            var h = graph.DefineFunction<Vec<Node>, long>("h", ValueLattice.Max<long>());
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            var top = root.Make(l.Of(of.Of(num.Of(2L))));
            root.Assume(h.Of(of.Of(num.Of(2L))).Is(7L));
            var three = root.Make(num.Of(3L));
            root.Assume(r.Of(of.Of(num.Of(4L))));
            var (two, four) = (root.Make(num.Of(2L)), root.Make(num.Of(4L)));
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                var made = default(Node);
                child.Write(w =>
                {
                    w.Union(one, two);
                    made = w.Make(l.Of(Vec.Of(one)));
                    w.Assume(h.Of(Vec.Of(one)).Is(9L));
                });
                Assert.True(child.AreEqual(made, top));
                Assert.Equal(9L, child.Get(h.Of(of.Of(num.Of(2L)))));

                child.Write(w =>
                {
                    w.Union(three, four);
                    w.Assume(r.Of(Vec.Of(three)));
                });
                Assert.True(child.Check(r.Of(of.Of(num.Of(4L)))));
                Assert.Equal(1, child.CountRows(r));
                Assert.Equal(1, child.CountRows(l));
                Assert.Equal(1, child.CountRows(h));
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            Assert.Equal(7L, root.Get(h.Of(of.Of(num.Of(2L)))));
            Assert.False(root.Check(r.Of(Vec.Of(three))));
            Assert.Equal(1, root.CountRows(r));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AChildsBatchFlushesOnceWhenItsConstantVectorsAreInItsView(bool frozen)
        {
            // Probe batch-held-constant.egg, which egglog at 5e655cae runs clean: (r (vec-of (Num 0))) and (Num 1) to
            // (Num 10) at the root; in a push five pairs of (union (Num 2i-1) (Num 2i)) and (r (vec-of (Num 0))), after
            // which the pairs are one and r keeps its one row. Here the push's commands are one batch of a child: its
            // constant vector is one the child's view holds, so no fact needs the unions before it rebuilt, and the
            // batch flushes once, at its end, moving the child's version as a batch of the unions alone moves a
            // sibling's.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var r = graph.DefineRelation<Vec<Node>>("r");
            var root = graph.Root;
            var zero = root.Make(num.Of(0L));
            root.Assume(r.Of(Primitives.VecOf<Node>().Of(num.Of(0L))));
            var nums = new Node[11];
            for (var i = 1; i <= 10; i++)
                nums[i] = root.Make(num.Of((long)i));
            if (frozen)
                root.Freeze();

            using var held = root.CreateChild();
            using var unions = root.CreateChild();
            var withConstants = Delta(held, true);
            var alone = Delta(unions, false);

            Assert.Equal(alone, withConstants);
            for (var i = 1; i <= 9; i += 2)
                Assert.True(held.AreEqual(nums[i], nums[i + 1]));
            Assert.Equal(1, held.CountRows(r));
            Assert.False(held.IsFaulted);
            held.Engine.Context.CheckInvariants();
            Assert.False(root.AreEqual(nums[1], nums[2]));

            long Delta(Scope child, bool constants)
            {
                var before = child.Version;
                child.Write(w =>
                {
                    for (var i = 1; i <= 9; i += 2)
                    {
                        w.Union(nums[i], nums[i + 1]);
                        if (constants)
                            w.Assume(r.Of(Vec.Of(zero)));
                    }
                });
                return child.Version - before;
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AChildsBatchWritesAConstantVectorThatACongruenceItsMergeDerivesRebuilt(bool frozen)
        {
            // Probe batch-derived-constant.egg, which egglog at 5e655cae runs clean: (A), (B), (F (A)) and (set (hold)
            // (vec-of (F (B)))) at the root; in a push (union (A) (B)) makes (F (A)) and (F (B)) one by congruence,
            // which rebuilds the held vector to (vec-of (F (A))), and then (r (vec-of (F (A)))) holds of it; after the
            // pop r has no row. Here the push's commands are one batch of a child: its view holds no such vector until
            // the child's rebuild has made the merge the union derives and rebuilt its copy of the vector, so the batch
            // flushes before the fact.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var f = graph.DefineConstructor<Node, Node>("F");
            graph.Values.RegisterVec<Node>();
            var hold = graph.DefineFunction<Vec<Node>>("hold");
            var r = graph.DefineRelation<Vec<Node>>("r");
            var root = graph.Root;
            var na = root.Make(a.Of());
            var nb = root.Make(b.Of());
            var fa = root.Make(f.Of(a.Of()));
            root.Assume(hold.Of().Is(Primitives.VecOf<Node>().Of(f.Of(b.Of()))));
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Write(w =>
                {
                    w.Union(na, nb);
                    w.Assume(r.Of(Vec.Of(fa)));
                });
                Assert.True(child.Check(r.Of(hold.Of())));
                Assert.Equal(1, child.CountRows(r));
                Assert.Equal(Vec.Of(fa), child.Get(hold.Of()));
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.Check(r.Of(hold.Of())));
            Assert.Equal(0, root.CountRows(r));
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

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EveryContainerOfClassesRebuildsInAChildAsInEgglogsPush(bool byRule)
        {
            // Probe push-sorts.egg, which egglog at 5e655cae runs clean: a set, a map, a multiset and two vectors of
            // classes made at the root, then in a push $vc = (vec-of (C)) of a class the push makes, held by (W $vc),
            // which the push interns, and (union (B) (C)) and (union (A) (B)), here the child's two host merges or one
            // rule's. The set shrinks, the map's keys collide and the later key's value stays, the multiset's counts
            // add, the three vectors become one and so do their holders' rows, and the rows keyed by the set and the
            // map are found by their new contents. After the pop the root has them as they were, and never held $vc.
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
            var vc = graph.DefineFunction<Vec<Node>>("vc");
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
            var vectors = graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>().Count;

            using (var child = root.CreateChild())
            {
                child.Assume(vc.Of().Is(Primitives.VecOf<Node>().Of(c.Of())));
                child.Make(holder.Of(vc.Of()));
                Assert.Equal(vectors, graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>().Count);
                if (byRule)
                {
                    child.Assume(go.Of());
                    child.Run(Schedule.Run(merge));
                }
                else
                {
                    child.Union(child.Make(b.Of()), child.Make(c.Of()));
                    child.Union(child.Make(a.Of()), child.Make(b.Of()));
                }

                Assert.False(child.IsFaulted);
                Assert.True(child.Check(Primitives.Count<Node>().Of(s.Of()).Is(1L)));
                Assert.True(child.Check(Primitives.MapLength<Node, long>().Of(m.Of()).Is(1L)));
                Assert.True(child.Check(Primitives.MapGet<Node, long>().Of(m.Of(), a.Of()).Is(20L)));
                Assert.True(child.Check(Primitives.MultiSetCount<Node>().Of(ms.Of(), a.Of()).Is(3L)));
                Assert.True(child.Check(va.Of().Is(vb.Of())));
                Assert.True(child.Check(vb.Of().Is(vc.Of())));
                Assert.True(child.Check(holder.Of(va.Of()).Is(holder.Of(vc.Of()))));
                Assert.True(child.Check(holder.Of(va.Of()).Is(holder.Of(vb.Of()))));
                Assert.True(child.Check(fs.Of(Primitives.SetOf<Node>().Of(a.Of())).Is(1L)));
                Assert.True(child.Check(fm.Of(insert.Of(empty.Of(), a.Of(), 20L)).Is(1L)));
                var one = child.Find(child.Make(a.Of()));
                Assert.Equal(Set.Of(one), child.Get(s.Of()));
                Assert.Equal(Map.Of((one, 20L)), child.Get(m.Of()));
                Assert.Equal(MultiSet.Of(one, one, one), child.Get(ms.Of()));
                Assert.Equal(Vec.Of(one), child.Get(vc.Of()));
                child.Engine.Context.CheckInvariants();
            }

            Assert.True(root.Check(Primitives.Count<Node>().Of(s.Of()).Is(2L)));
            Assert.False(root.TryGet(vc.Of(), out _));
            Assert.Equal(vectors, graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>().Count);
            Assert.False(root.Check(va.Of().Is(vb.Of())));
            Assert.False(root.Check(holder.Of(va.Of()).Is(holder.Of(vb.Of()))));
        }

        /// <summary>
        /// egglog's tests/container-rebuild.egg at 5e655cae, its first block, in a child of a root that is frozen or
        /// writable: the vectors the push makes, of classes it makes, become one when their classes merge, a
        /// constructor over one is found over the other, and a vector made of classes that merge with newer ones later
        /// is the one a vector of those newer classes reads as. egglog runs the file clean. The datatype, the vector
        /// sort, the constructor and the three lets, nullary functions as egglog's lets are, are defined before the
        /// push, as the library defines its types up front; everything else is the push's. After the pop the root holds
        /// none of it.
        /// </summary>
        /// <param name="frozen">Whether the root is frozen.</param>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ContainerRebuildsFirstBlockRunsInAChild(bool frozen)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var myVec = graph.DefineConstructor<Vec<Node>, Node>("MyVec");
            var v1 = graph.DefineFunction<Vec<Node>>("v1");
            var v2 = graph.DefineFunction<Vec<Node>>("v2");
            var v3 = graph.DefineFunction<Vec<Node>>("v3");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                // (let $v1 (vec-of (Num 1) (Num 2))) (let $v2 (vec-of (Num 2) (Num 2))) (union (Num 1) (Num 2))
                child.Assume(v1.Of().Is(of.Of(num.Of(1L), num.Of(2L))));
                child.Assume(v2.Of().Is(of.Of(num.Of(2L), num.Of(2L))));
                Assert.False(child.Check(v1.Of().Is(v2.Of())));
                child.Union(child.Make(num.Of(1L)), child.Make(num.Of(2L)));

                // (check (= $v1 $v2)) (MyVec $v1) (check (MyVec $v2)) (check (= (MyVec $v1) (MyVec $v2)))
                Assert.True(child.Check(v1.Of().Is(v2.Of())));
                child.Make(myVec.Of(v1.Of()));
                Assert.True(child.Check(Fact.Exists(myVec.Of(v2.Of()))));
                Assert.True(child.Check(myVec.Of(v1.Of()).Is(myVec.Of(v2.Of()))));

                // (let $v3 (vec-of (Num 4) (Num 5))) (union (Num 4) (Num 6)) (union (Num 5) (Num 7))
                child.Assume(v3.Of().Is(of.Of(num.Of(4L), num.Of(5L))));
                child.Union(child.Make(num.Of(4L)), child.Make(num.Of(6L)));
                child.Union(child.Make(num.Of(5L)), child.Make(num.Of(7L)));

                // (fail (check (= (MyVec $v3) (MyVec (vec-of (Num 6) (Num 7)))))), (MyVec $v3), then the check again.
                Assert.False(child.Check(myVec.Of(v3.Of()).Is(myVec.Of(of.Of(num.Of(6L), num.Of(7L))))));
                child.Make(myVec.Of(v3.Of()));
                Assert.True(child.Check(myVec.Of(v3.Of()).Is(myVec.Of(of.Of(num.Of(6L), num.Of(7L))))));
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.TryGet(v1.Of(), out _));
            Assert.False(root.Check(Fact.Exists(num.Of(1L))));
            Assert.Equal(0, graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>().Count);
            root.Engine.Context.CheckInvariants();
        }

        /// <summary>
        /// egglog's tests/container-rebuild.egg at 5e655cae, its second block, in a child of a root that is frozen or
        /// writable: a constructor over a vector the push made, after a union that rebuilds the vector, extracts as
        /// egglog prints it, <c>(MyVec (vec-of (Num 1) (Num 1)))</c>, and the rebuild makes it the constructor over the
        /// other vector. Definitions are made before the push, as in the first block's port.
        /// </summary>
        /// <param name="frozen">Whether the root is frozen.</param>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ContainerRebuildsSecondBlockRunsInAChild(bool frozen)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var myVec = graph.DefineConstructor<Vec<Node>, Node>("MyVec");
            var v1 = graph.DefineFunction<Vec<Node>>("v1");
            var v2 = graph.DefineFunction<Vec<Node>>("v2");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Assume(v1.Of().Is(of.Of(num.Of(1L), num.Of(2L))));
                child.Assume(v2.Of().Is(of.Of(num.Of(2L), num.Of(2L))));
                child.Union(child.Make(num.Of(1L)), child.Make(num.Of(2L)));
                var made = child.Make(myVec.Of(v1.Of()));
                Assert.Equal("(MyVec (vec-of (Num 1) (Num 1)))", child.Extract(made).ToString());
                Assert.True(child.Check(myVec.Of(v1.Of()).Is(myVec.Of(v2.Of()))));
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            Assert.Equal(0, graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>().Count);
        }

        /// <summary>
        /// egglog's tests/container-rebuild.egg at 5e655cae, its third block, in a child of a root that is frozen or
        /// writable: a vector of an expensive class, which a union in the push makes the class of a constructor over a
        /// cheaper vector, extracts its element through that constructor. egglog prints <c>(vec-of (VecContainer
        /// (vec-of (Add 1 2))))</c> for <c>(extract $myvec 0)</c>; the library extracts classes, not vectors, so the
        /// vector is read and its one element extracted, which must be egglog's element. Definitions are made before
        /// the push, as in the first block's port.
        /// </summary>
        /// <param name="frozen">Whether the root is frozen.</param>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ContainerRebuildsThirdBlockRunsInAChild(bool frozen)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var add = graph.DefineConstructor<long, long, Node>("Add");
            var expensive = graph.DefineConstructor<Node>("Expensive", cost: 100);
            graph.Values.RegisterVec<Node>();
            var vecContainer = graph.DefineConstructor<Vec<Node>, Node>("VecContainer");
            var myvec = graph.DefineFunction<Vec<Node>>("myvec");
            var cheapvec = graph.DefineFunction<Vec<Node>>("cheapvec");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            if (frozen)
                root.Freeze();

            using (var child = root.CreateChild())
            {
                child.Assume(myvec.Of().Is(of.Of(expensive.Of())));
                child.Assume(cheapvec.Of().Is(of.Of(add.Of(1L, 2L))));
                var container = child.Make(vecContainer.Of(cheapvec.Of()));
                child.Union(container, child.Make(expensive.Of()));
                var element = Assert.Single(child.Get(myvec.Of()));
                Assert.Equal("(VecContainer (vec-of (Add 1 2)))", child.Extract(element).ToString());
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            Assert.Equal(0, graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>().Count);
        }

        /// <summary>
        /// egglog's tests/container-fail.egg at 5e655cae, its push block, in a child of a writable root, and then the
        /// root's block after the pop (issue #831): in the push, <c>$rebuilt = (l (vec-of (w (k (b)))))</c> of classes
        /// and a vector the push makes; the rewrite of <c>(w x)</c> to <c>x</c> rebuilds the vector to
        /// <c>(vec-of (k (b)))</c>, whose match the rewrite of <c>(l (vec-of (k x)))</c> to <c>x</c> must revisit, so
        /// saturation makes <c>$rebuilt</c> <c>(b)</c>. After the pop the root, which never held any of it, resolves
        /// its own <c>(l (vec-of (k (b))))</c> to <c>(b)</c>. egglog runs the file clean. The sorts, constructors and
        /// rules are defined before the push.
        /// </summary>
        [Fact]
        public void ContainerFailsPushBlockRunsInAChild()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("E");
            graph.Values.RegisterVec<Node>();
            var b = graph.DefineConstructor<Node>("b");
            var k = graph.DefineConstructor<Node, Node>("k");
            var w = graph.DefineConstructor<Node, Node>("w");
            var l = graph.DefineConstructor<Vec<Node>, Node>("l");
            var of = Primitives.VecOf<Node>();
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("w", r =>
            {
                var (e, x) = r.Vars<Node, Node>("e", "x");
                r.When(e.Is(w.Of(x)));
                r.Then(e.Is(x));
            });
            rules.DefineRule("l", r =>
            {
                var (e, x, y) = r.Vars<Node, Node, Node>("e", "x", "y");
                var v = r.Var<Vec<Node>>("v");
                r.When(e.Is(l.Of(v)), y.Is(k.Of(x)), v.Is(of.Of(y)));
                r.Then(e.Is(x));
            });
            var root = graph.Root;

            using (var child = root.CreateChild())
            {
                var rebuilt = child.Make(l.Of(of.Of(w.Of(k.Of(b.Of())))));
                child.Run(Schedule.Saturate(Schedule.Run(rules)));
                Assert.True(child.AreEqual(rebuilt, b.Of()));
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(root.Check(Fact.Exists(b.Of())));
            Assert.Equal(0, graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>().Count);
            var resolved = root.Make(l.Of(of.Of(k.Of(b.Of()))));
            root.Run(Schedule.Saturate(Schedule.Run(rules)));
            Assert.True(root.AreEqual(resolved, b.Of()));
            root.Engine.Context.CheckInvariants();
        }

        /// <summary>
        /// Siblings under a frozen root, each on its own thread, make the same new vector of the root's classes at
        /// once: each interns it in its own containers, under an id local to it, so the two ids differ and neither
        /// sibling, nor the root, sees the other's. The expectation is egglog's at 5e655cae running probe
        /// siblings-equal-vector.egg, which exits clean: (Num 1) and (Num 2); twice a push that sets (g) to (vec-of
        /// (Num 1) (Num 2)), makes (L (vec-of (Num 1) (Num 2))) and checks (= (L (g)) (L (vec-of (Num 1) (Num 2))));
        /// after the pops g has no value. egglog's pushes run one after the other; here they run at once, each over the
        /// frozen root.
        /// </summary>
        [Fact]
        public void SiblingsOfAFrozenRootMakeEqualVectorsEachInItsOwnContainers()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var l = graph.DefineConstructor<Vec<Node>, Node>("L");
            var g = graph.DefineFunction<Vec<Node>>("g");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            root.Make(num.Of(1L));
            root.Make(num.Of(2L));
            root.Freeze();
            var interned = graph.Values.InternedCount;
            var env = graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>();

            for (var round = 0; round < 10; round++)
            {
                var children = new[] { root.CreateChild(), root.CreateChild() };
                var made = new Node[children.Length];
                Parallel.For(0, children.Length, new ParallelOptions { MaxDegreeOfParallelism = children.Length }, i =>
                {
                    var child = children[i];
                    child.Assume(g.Of().Is(of.Of(num.Of(1L), num.Of(2L))));
                    made[i] = child.Make(l.Of(of.Of(num.Of(1L), num.Of(2L))));
                    Assert.True(child.Check(l.Of(g.Of()).Is(l.Of(of.Of(num.Of(1L), num.Of(2L))))));
                    Assert.False(child.IsFaulted);
                });

                var ids = new uint[children.Length];
                for (var i = 0; i < children.Length; i++)
                {
                    var engine = children[i].Engine.Context;
                    var layer = (Relations.ContainerLayer<Relations.VecContainer>)engine.Context.ContainerLayers!.LayerAt(env.TypeId)!;
                    ids[i] = Assert.Single(Entries(layer.Own));
                    Assert.True(engine.Context.IsLocalId(ids[i]));
                    engine.CheckInvariants();
                }

                Assert.NotEqual(ids[0], ids[1]);
                Assert.Equal(interned, graph.Values.InternedCount);
                foreach (var child in children)
                    child.Dispose();
            }

            Assert.False(root.TryGet(g.Of(), out _));
            root.Engine.Context.CheckInvariants();

            static List<uint> Entries(Relations.ContainerEnv<Relations.VecContainer> own)
            {
                var entries = new List<Relations.ContainerEntry<Relations.VecContainer>>();
                var collector = new Relations.ContainerEntryCollector<Relations.VecContainer>(entries);
                own.ForEach(ref collector);
                return entries.Select(entry => entry.Id).ToList();
            }
        }

        /// <summary>
        /// Disposing a child drops the vectors it made, and leaves its parent, its sibling and the root as they were: a
        /// writable child of the root, frozen or writable, makes a vector of its own; under it, a child makes vectors
        /// of classes it makes and of the root's, one equal to its parent's once its union rebuilds that, which it then
        /// holds under its parent's id; once it is disposed, the parent, the sibling and the root serialize as before
        /// it was made, and the graph's pool and the parent's own containers hold as many vectors. The expectation is
        /// egglog's at 5e655cae running probe child-intern-pop.egg, which exits clean: (Num 1) and (set (f) (vec-of
        /// (Num 1))); in a push (set (g 1) (vec-of (Num 1) (Num 2))); in a nested push (set (g 2) (vec-of (Num 3))), (L
        /// (vec-of (Num 1) (Num 2))), (L (vec-of (Num 1) (Num 1))) and (union (Num 2) (Num 1)), after which the two L
        /// terms are one and (g 1) is (vec-of (Num 1) (Num 1)); after the inner pop (g 1) is (vec-of (Num 1) (Num 2))
        /// again, and g 2, the second L term and the union are gone; after the outer pop g has no value and (f) is as
        /// it was.
        /// </summary>
        /// <param name="frozen">Whether the root is frozen.</param>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DisposingAChildThatMadeVectorsDropsThemAndLeavesItsParentSiblingAndRootAsTheyWere(bool frozen)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var l = graph.DefineConstructor<Vec<Node>, Node>("L");
            var f = graph.DefineFunction<Vec<Node>>("f");
            var g = graph.DefineFunction<long, Vec<Node>>("g");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            root.Assume(f.Of().Is(of.Of(num.Of(1L))));
            if (frozen)
                root.Freeze();

            var vectors = graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>();
            using var parent = root.CreateChild();
            parent.Assume(g.Of(1L).Is(of.Of(num.Of(1L), num.Of(2L))));
            using var sibling = parent.CreateChild();
            sibling.Make(num.Of(5L));
            var rootBefore = root.Serialize().ToJson();
            var parentBefore = parent.Serialize().ToJson();
            var siblingBefore = sibling.Serialize().ToJson();
            var interned = graph.Values.InternedCount;
            var parentOwn = parent.Engine.Context.Context.ContainerLayers!.LayerAt(vectors.TypeId)!.OwnBase.Count;
            Assert.Equal(1, parentOwn);

            using (var child = parent.CreateChild())
            {
                child.Assume(g.Of(2L).Is(of.Of(num.Of(3L))));
                var held = child.Make(l.Of(of.Of(num.Of(1L), num.Of(2L))));
                var made = child.Make(l.Of(of.Of(num.Of(1L), num.Of(1L))));
                Assert.False(child.AreEqual(held, made));
                child.Union(child.Make(num.Of(2L)), one);
                Assert.True(child.AreEqual(held, made));
                Assert.True(child.Check(g.Of(1L).Is(of.Of(num.Of(1L), num.Of(1L)))));
                Assert.True(child.Check(g.Of(2L).Is(of.Of(num.Of(3L)))));
                Assert.False(child.IsFaulted);
                child.Engine.Context.CheckInvariants();
            }

            Assert.Equal(parentBefore, parent.Serialize().ToJson());
            Assert.Equal(siblingBefore, sibling.Serialize().ToJson());
            Assert.Equal(rootBefore, root.Serialize().ToJson());
            Assert.Equal(interned, graph.Values.InternedCount);
            Assert.Equal(parentOwn, parent.Engine.Context.Context.ContainerLayers!.LayerAt(vectors.TypeId)!.OwnBase.Count);
            Assert.True(parent.Check(g.Of(1L).Is(of.Of(num.Of(1L), num.Of(2L)))));
            Assert.False(parent.TryGet(g.Of(2L), out _));
            Assert.False(parent.Check(Fact.Exists(l.Of(of.Of(num.Of(1L), num.Of(2L))))));
            Assert.False(sibling.AreEqual(one, sibling.Make(num.Of(2L))));
            parent.Engine.Context.CheckInvariants();
            sibling.Engine.Context.CheckInvariants();
            root.Engine.Context.CheckInvariants();
        }

        /// <summary>
        /// Three writable levels under a root, frozen or writable: the top one makes a vector, or a multiset, of one
        /// class first, which reserves its id block before the middle one's; the middle one then makes <c>[4, 2, 2]</c>
        /// in its own containers, the leaf merges (Num 1) into (Num 4), which rebuilds its copy of it to
        /// <c>[1, 2, 2]</c>, and the top one makes <c>[4, 2, 2]</c> too, in its own containers under the smaller id. The
        /// middle one, when it reads, merges its id into the top one's, and the leaf, whose own row of (fv 202) names
        /// the middle one's id while a copy of the middle one's rewritten row waits to merge, rewrites its row under the
        /// top one's id: the two rows meet with one value, though fv has no merge. egglog has no writable level under a
        /// live push, so the expectation is egglog's at 5e655cae running the commands in order, probes
        /// nested-collision.egg and nested-collision-multiset.egg, which exit clean: (= (fv 202) (fv 103)), and (fv
        /// 202) is the rebuilt content; a fresh root replaying them agrees.
        /// </summary>
        /// <param name="multiset">Whether the content is a multiset rather than a vector.</param>
        /// <param name="frozen">Whether the root is frozen.</param>
        /// <param name="middleFirst">Whether the middle level reads before the leaf.</param>
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void AContentAHigherWritableLevelMakesAfterALowerOneMergesWithItInTheLeafThatRebuiltIt(bool multiset, bool frozen, bool middleFirst)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterMultiSet<Node>();
            var fv = graph.DefineFunction<long, Vec<Node>>("fv");
            var fms = graph.DefineFunction<long, MultiSet<Node>>("fms");
            Fact One(long key, long x) => multiset
                ? fms.Of(key).Is(Primitives.MultiSetOf<Node>().Of(num.Of(x)))
                : fv.Of(key).Is(Primitives.VecOf<Node>().Of(num.Of(x)));
            Fact Three(long key, long x, long y, long z) => multiset
                ? fms.Of(key).Is(Primitives.MultiSetOf<Node>().Of(num.Of(x), num.Of(y), num.Of(z)))
                : fv.Of(key).Is(Primitives.VecOf<Node>().Of(num.Of(x), num.Of(y), num.Of(z)));
            Fact Same(long a, long b) => multiset ? fms.Of(a).Is(fms.Of(b)) : fv.Of(a).Is(fv.Of(b));

            var root = graph.Root;
            for (var i = 0L; i < 6; i++)
                root.Make(num.Of(i));

            if (frozen)
                root.Freeze();

            using var top = root.CreateChild();
            using var middle = top.CreateChild();
            top.Assume(One(1L, 3L));
            using var leaf = middle.CreateChild();
            middle.Assume(Three(202L, 4L, 2L, 2L));
            leaf.Union(leaf.Make(num.Of(1L)), leaf.Make(num.Of(4L)));
            top.Assume(Three(103L, 4L, 2L, 2L));

            // The top level's ids come from a block reserved before the middle one's.
            var topIds = OwnIds(top);
            var middleId = Assert.Single(OwnIds(middle));
            Assert.Equal(2, topIds.Count);
            Assert.All(topIds, id => Assert.True(id < middleId));

            if (middleFirst)
                Assert.True(middle.Check(Same(202L, 103L)));

            Assert.True(leaf.Check(Same(202L, 103L)));
            Assert.True(leaf.Check(Three(202L, 1L, 2L, 2L)));
            Assert.False(leaf.IsFaulted);
            Assert.True(middle.Check(Same(202L, 103L)));
            Assert.True(middle.Check(Three(202L, 4L, 2L, 2L)));
            Assert.True(top.Check(Three(103L, 4L, 2L, 2L)));
            Assert.False(top.Check(Three(103L, 1L, 2L, 2L)));
            leaf.Engine.Context.CheckInvariants();
            middle.Engine.Context.CheckInvariants();
            top.Engine.Context.CheckInvariants();

            // A fresh root replaying the commands in order.
            var replayGraph = new EGraph();
            replayGraph.DefineSort<Node>("Math");
            var replayNum = replayGraph.DefineConstructor<long, Node>("Num");
            replayGraph.Values.RegisterVec<Node>();
            replayGraph.Values.RegisterMultiSet<Node>();
            var replayFv = replayGraph.DefineFunction<long, Vec<Node>>("fv");
            var replayFms = replayGraph.DefineFunction<long, MultiSet<Node>>("fms");
            Fact ReplayOne(long key, long x) => multiset
                ? replayFms.Of(key).Is(Primitives.MultiSetOf<Node>().Of(replayNum.Of(x)))
                : replayFv.Of(key).Is(Primitives.VecOf<Node>().Of(replayNum.Of(x)));
            Fact ReplayThree(long key, long x, long y, long z) => multiset
                ? replayFms.Of(key).Is(Primitives.MultiSetOf<Node>().Of(replayNum.Of(x), replayNum.Of(y), replayNum.Of(z)))
                : replayFv.Of(key).Is(Primitives.VecOf<Node>().Of(replayNum.Of(x), replayNum.Of(y), replayNum.Of(z)));
            var replay = replayGraph.Root;
            for (var i = 0L; i < 6; i++)
                replay.Make(replayNum.Of(i));

            replay.Assume(ReplayOne(1L, 3L));
            replay.Assume(ReplayThree(202L, 4L, 2L, 2L));
            replay.Union(replay.Make(replayNum.Of(1L)), replay.Make(replayNum.Of(4L)));
            replay.Assume(ReplayThree(103L, 4L, 2L, 2L));
            Assert.True(replay.Check(multiset ? replayFms.Of(202L).Is(replayFms.Of(103L)) : replayFv.Of(202L).Is(replayFv.Of(103L))));
            Assert.True(replay.Check(ReplayThree(202L, 1L, 2L, 2L)));
            Assert.Equal(replay.CountRows(replayFv), leaf.CountRows(fv));
            Assert.Equal(replay.CountRows(replayFms), leaf.CountRows(fms));
            Assert.Equal(3, leaf.CountRows(fv) + leaf.CountRows(fms));

            List<uint> OwnIds(Scope scope)
            {
                var layers = scope.Engine.Context.Context.ContainerLayers!;
                return multiset
                    ? Ids(layers, graph.Engine.ContainerValues.GetEnvironment<Relations.MultiSetContainer>())
                    : Ids(layers, graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>());
            }

            static List<uint> Ids<TContainer>(Relations.ContainerLayers layers, Relations.ContainerEnv<TContainer> env)
                where TContainer : struct, Relations.IContainerValue<TContainer>
            {
                var entries = new List<Relations.ContainerEntry<TContainer>>();
                var collector = new Relations.ContainerEntryCollector<TContainer>(entries);
                layers.LayerOf(env)!.Own.ForEach(ref collector);
                return entries.Select(entry => entry.Id).ToList();
            }
        }

        /// <summary>
        /// A child's union and its writable ancestor's later one together rebuild a vector of the child's view to a
        /// content equal to another vector of the ancestor's, which has the smaller id: the root's (fv 1000) =
        /// <c>[4, 5]</c>, or a writable child's that a grandchild sees, becomes <c>[1, 2]</c>, which (fv 0) holds. The
        /// child's union rebuilds its copy and re-stamps its row of (fv 1000); the ancestor's union rebuilds the
        /// ancestor's vector in place, and the child's catch-up keeps a copy of the ancestor's re-stamped row beside its
        /// own; its container pass then merges its copy's id into (fv 0)'s, and the tables' pass rewrites its own row
        /// under (fv 0)'s id, which the copy meets with one value, though fv has no merge. The expectation is egglog's
        /// at 5e655cae running probe catchup-collision.egg, which exits clean: in a push (union (Num 2) (Num 5)) and
        /// (union (Num 1) (Num 4)), after which (= (fv 1000) (fv 0)) and (fv 1000) is (vec-of (Num 1) (Num 2)); the
        /// same with (fv 1000) set in the push.
        /// </summary>
        /// <param name="childMade">Whether (fv 1000) is a writable child's, seen by its child, rather than the
        /// root's.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AVectorAChildAndItsWritableAncestorRebuildTogetherMergesWithTheAncestorsEqualOne(bool childMade)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var fv = graph.DefineFunction<long, Vec<Node>>("fv");
            var of = Primitives.VecOf<Node>();
            var root = graph.Root;
            for (var i = 0L; i < 6; i++)
                root.Make(num.Of(i));

            Scope ancestor;
            Scope child;
            if (childMade)
            {
                ancestor = root.CreateChild();
                root.Assume(fv.Of(0L).Is(of.Of(num.Of(1L), num.Of(2L))));
                child = ancestor.CreateChild();
                ancestor.Assume(fv.Of(1000L).Is(of.Of(num.Of(4L), num.Of(5L))));
            }
            else
            {
                ancestor = root;
                root.Assume(fv.Of(0L).Is(of.Of(num.Of(1L), num.Of(2L))));
                root.Assume(fv.Of(1000L).Is(of.Of(num.Of(4L), num.Of(5L))));
                child = root.CreateChild();
            }

            child.Union(child.Make(num.Of(2L)), child.Make(num.Of(5L)));
            ancestor.Union(ancestor.Make(num.Of(1L)), ancestor.Make(num.Of(4L)));

            Assert.True(child.Check(fv.Of(1000L).Is(fv.Of(0L))));
            Assert.True(child.Check(fv.Of(1000L).Is(of.Of(num.Of(1L), num.Of(2L)))));
            Assert.Equal(2, child.CountRows(fv));
            Assert.False(child.IsFaulted);
            child.Engine.Context.CheckInvariants();

            // The ancestor rebuilt its own vector only, as egglog's state before the push's union of (Num 2) and (Num 5).
            Assert.True(ancestor.Check(fv.Of(1000L).Is(of.Of(num.Of(1L), num.Of(5L)))));
            Assert.False(ancestor.Check(fv.Of(1000L).Is(fv.Of(0L))));
            ancestor.Engine.Context.CheckInvariants();
            child.Dispose();
            if (childMade)
                ancestor.Dispose();
        }

        /// <summary>
        /// A child's union makes nested containers its writable ancestor makes later equal to ones the child's view
        /// holds: after the child's (union (Num 1) (Num 2)), its sets of (fvv 122) to <c>[[1], [1]]</c> and of
        /// (fvv 133) to <c>[[2], [2]]</c>, both of which find the root's <c>[[1], [1]]</c>, and its set of (fvv 144) to
        /// <c>[[2], [0]]</c>, which it interns as <c>[[1], [0]]</c>, the root sets each key to its own new container of
        /// (Num 2) (<c>[[2], [2]]</c> twice and <c>[[2], [0]]</c>); or the same with sets of pairs, <c>{(1, 3)}</c>,
        /// <c>{(2, 3)}</c> and <c>{(2, 0)}</c>. The child's catch-up keeps a copy of each of the root's rows beside its
        /// own. Its container pass merges the root's inner containers into the child's equal ones, and the outer one
        /// rebuilds equal to the child's only in the container pass after that, which the child runs before the copies
        /// merge, though the functions have no merge. The expectations are egglog's at 5e655cae running probes
        /// nested-catchup.egg and nested-catchup-local.egg, which exit clean: in a push (union (Num 1) (Num 2)), then
        /// each key set to its two contents, after which (fvv 122) and (fvv 133) equal (fvv 111), or (fps 122) and
        /// (fps 133) equal (fps 111), and (fvv 144) is <c>[[1], [0]]</c>, or (fps 144) is <c>{(1, 0)}</c>.
        /// </summary>
        /// <param name="pairs">Whether the containers are sets of pairs rather than vectors of vectors.</param>
        /// <param name="grandchild">Whether a child of the child, made before the root's sets, reads first.</param>
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void NestedContainersAWritableAncestorMakesAfterAChildsUnionMergeWithTheChildsEqualOnes(bool pairs, bool grandchild)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterVec<Vec<Node>>();
            graph.Values.RegisterPair<Node, Node>();
            graph.Values.RegisterSet<Pair<Node, Node>>();
            var fvv = graph.DefineFunction<long, Vec<Vec<Node>>>("fvv");
            var fps = graph.DefineFunction<long, Set<Pair<Node, Node>>>("fps");
            var root = graph.Root;
            for (var i = 0L; i < 4; i++)
                root.Make(num.Of(i));

            root.Assume(Is(num, 111L, 1L));
            using var child = root.CreateChild();
            child.Union(child.Make(num.Of(1L)), child.Make(num.Of(2L)));
            child.Assume(Is(num, 122L, 1L));
            child.Assume(Is(num, 133L, 2L));
            Assert.True(child.Engine.Context.Context.ContainerLayers!.IsEmpty);
            child.Assume(Is(num, 144L, 2L, 0L));
            Assert.False(child.Engine.Context.Context.ContainerLayers!.IsEmpty);
            using var leaf = grandchild ? child.CreateChild() : null;
            root.Assume(Is(num, 122L, 2L));
            root.Assume(Is(num, 133L, 2L));
            root.Assume(Is(num, 144L, 2L, 0L));

            if (leaf is not null)
            {
                Assert.True(leaf.Check(Same(122L, 111L)));
                Assert.True(leaf.Check(Same(133L, 111L)));
                Assert.True(leaf.Check(Is(num, 144L, 1L, 0L)));
                Assert.False(leaf.IsFaulted);
                leaf.Engine.Context.CheckInvariants();
            }

            Assert.True(child.Check(Same(122L, 111L)));
            Assert.True(child.Check(Same(133L, 111L)));
            Assert.True(child.Check(Is(num, 144L, 1L, 0L)));
            Assert.False(child.IsFaulted);
            child.Engine.Context.CheckInvariants();
            Assert.False(root.Check(Same(122L, 111L)));
            Assert.True(root.Check(Same(122L, 133L)));

            // A fresh root replaying the commands in order.
            var replayGraph = new EGraph();
            replayGraph.DefineSort<Node>("Math");
            var replayNum = replayGraph.DefineConstructor<long, Node>("Num");
            replayGraph.Values.RegisterVec<Node>();
            replayGraph.Values.RegisterVec<Vec<Node>>();
            replayGraph.Values.RegisterPair<Node, Node>();
            replayGraph.Values.RegisterSet<Pair<Node, Node>>();
            var replayFvv = replayGraph.DefineFunction<long, Vec<Vec<Node>>>("fvv");
            var replayFps = replayGraph.DefineFunction<long, Set<Pair<Node, Node>>>("fps");
            var replay = replayGraph.Root;
            for (var i = 0L; i < 4; i++)
                replay.Make(replayNum.Of(i));

            replay.Assume(Is(replayNum, 111L, 1L, vv: replayFvv, ps: replayFps));
            replay.Union(replay.Make(replayNum.Of(1L)), replay.Make(replayNum.Of(2L)));
            replay.Assume(Is(replayNum, 122L, 1L, vv: replayFvv, ps: replayFps));
            replay.Assume(Is(replayNum, 133L, 2L, vv: replayFvv, ps: replayFps));
            replay.Assume(Is(replayNum, 144L, 2L, 0L, vv: replayFvv, ps: replayFps));
            replay.Assume(Is(replayNum, 122L, 2L, vv: replayFvv, ps: replayFps));
            replay.Assume(Is(replayNum, 133L, 2L, vv: replayFvv, ps: replayFps));
            replay.Assume(Is(replayNum, 144L, 2L, 0L, vv: replayFvv, ps: replayFps));
            Assert.True(replay.Check(pairs ? replayFps.Of(122L).Is(replayFps.Of(111L)) : replayFvv.Of(122L).Is(replayFvv.Of(111L))));
            Assert.True(replay.Check(pairs ? replayFps.Of(133L).Is(replayFps.Of(111L)) : replayFvv.Of(133L).Is(replayFvv.Of(111L))));
            Assert.True(replay.Check(Is(replayNum, 144L, 1L, 0L, vv: replayFvv, ps: replayFps)));
            Assert.Equal(4, replay.CountRows(pairs ? replayFps : replayFvv));
            Assert.Equal(4, child.CountRows(pairs ? fps : fvv));
            if (leaf is not null)
                Assert.Equal(4, leaf.CountRows(pairs ? fps : fvv));

            // The content of a key: [[x], [x]] or {(x, 3)}, or with y, [[x], [y]] or {(x, y)}.
            Fact Is(Constructor<long, Node> n, long key, long x, long? y = null, Function<long, Vec<Vec<Node>>>? vv = null, Function<long, Set<Pair<Node, Node>>>? ps = null) => pairs
                ? (ps ?? fps).Of(key).Is(Primitives.SetOf<Pair<Node, Node>>().Of(Primitives.PairOf<Node, Node>().Of(n.Of(x), n.Of(y ?? 3L))))
                : (vv ?? fvv).Of(key).Is(Primitives.VecOf<Vec<Node>>().Of(Primitives.VecOf<Node>().Of(n.Of(x)), Primitives.VecOf<Node>().Of(n.Of(y ?? x))));

            Fact Same(long a, long b) => pairs ? fps.Of(a).Is(fps.Of(b)) : fvv.Of(a).Is(fvv.Of(b));
        }

        /// <summary>
        /// A query on a frozen scope interns nothing: one that binds a new vector of classes, which no join gives, to a
        /// variable of its own is refused, on a frozen child as on the frozen root, each with its own message, and the
        /// frozen child's containers stay empty.
        /// </summary>
        /// <param name="child">Whether the frozen scope is a child of the frozen root.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFrozenScopesQueryRefusesANewVectorItWouldBind(bool child)
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("E");
            graph.Values.RegisterVec<Node>();
            var b = graph.DefineConstructor<Node>("b");
            var k = graph.DefineConstructor<Node, Node>("k");
            var root = graph.Root;
            root.Make(k.Of(b.Of()));
            root.Freeze();
            using var frozenChild = child ? root.CreateChild() : null;
            frozenChild?.Freeze();
            var scope = frozenChild ?? root;

            var query = graph.DefineQuery("pairs", q =>
            {
                var y = q.Var<Node>("y");
                var v = q.Var<Vec<Node>>("v");
                q.When(y.Is(k.Of(q.Var<Node>("x"))), v.Is(Primitives.VecOf<Node>().Of(y, y)));
            });
            var interned = graph.Values.InternedCount;

            var error = Assert.Throws<RuleEvaluationException>(() => scope.Count(query));
            var refusal = Assert.IsType<UninternedContainerException>(error.InnerException);
            Assert.Contains(child ? $"which frozen scope '{scope.Path}' cannot intern" : "which the frozen root cannot intern", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(interned, graph.Values.InternedCount);
            if (frozenChild is not null)
                Assert.True(frozenChild.Engine.Context.Context.ContainerLayers!.IsEmpty);
        }

        /// <summary>
        /// A split's assumptions may hold a constant vector of classes from the root, which each case interns in its own
        /// containers when its view does not hold it (SplitTests checks the cases against egglog's push), but not one
        /// that holds a class of a child scope, as no split rule holds a class constant of one: that split is refused at
        /// its definition, naming it, and nothing is registered.
        /// </summary>
        [Fact]
        public void ASplitsAssumptionHoldsAConstantVectorOfTheRootsClassesButNotOfAChildsClass()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var h = graph.DefineFunction<string, Vec<Node>>("h");
            var choice = graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var one = graph.Root.Make(num.Of(1L));
            using var child = graph.Root.CreateChild();
            var two = child.Make(num.Of(2L));

            void Define(string name, Vec<Node> constant) => graph.DefineSplit(name, s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)), h.Of(k).Is(constant));
            });

            Define("pick", Vec.Of(one));
            var error = Assert.Throws<RuleDefinitionException>(() => Define("pick a child's", Vec.Of(one, two)));
            Assert.Equal("pick a child's", error.RuleName);
            Assert.Equal($"a constant {graph.Values.TypeOf<Vec<Node>>().Name} is not one a split rule may hold: class {two.Id} came from scope "
                + $"'{child.Path}', which is not '{graph.Root.Path}' or an ancestor of it", error.Message);
            Assert.Equal("pick", Assert.Single(graph.SplitRules).Name);
        }

        /// <summary>
        /// A split's assumptions may not hold a constant function value, alone or in a vector, that the root could not
        /// write: a default value, which applies nothing, or one that names a table of another graph. Each split is
        /// refused at its definition with the reason the value is refused, and none of them is registered. egglog has
        /// neither a default function value nor a second graph, so the expectation is the host's own refusal of the value.
        /// </summary>
        [Fact]
        public void ASplitsAssumptionRefusesAConstantFunctionValueTheRootCouldNotWrite()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterFn<Node, Node>();
            graph.Values.RegisterVec<UnstableFn<Node, Node>>();
            var add = graph.DefineConstructor<Node, Node, Node>("Add");
            var hf = graph.DefineFunction<string, UnstableFn<Node, Node>>("hf");
            var hv = graph.DefineFunction<string, Vec<UnstableFn<Node, Node>>>("hv");
            var choice = graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var one = graph.Root.Make(num.Of(1L));
            var other = new EGraph();
            other.DefineSort<Node>("Math");
            var otherAdd = other.DefineConstructor<Node, Node, Node>("Add");

            void Define<T>(string name, Function<string, T> function, T constant) where T : notnull =>
                graph.DefineSplit(name, s =>
                {
                    var (k, d) = s.Vars<string, Set<long>>("k", "d");
                    s.When(d.Is(choice.Of(k)));
                    var v = s.Cases("v", d);
                    s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)), function.Of(k).Is(constant));
                });

            Define("pick", hf, UnstableFn.Of<Node, Node>(add, one));
            var fnName = graph.Values.TypeOf<UnstableFn<Node, Node>>().Name;
            var vecName = graph.Values.TypeOf<Vec<UnstableFn<Node, Node>>>().Name;
            var foreign = UnstableFn.Of<Node, Node>(otherAdd, one);
            var cases = new (string Name, Action Define, string Message)[]
            {
                ("default", () => Define("default", hf, default),
                    $"a constant {fnName} is not one a split rule may hold: A default {fnName} applies nothing; make one with "
                    + "UnstableFn.Of or unstable-fn"),
                ("another graph's", () => Define("another graph's", hf, foreign),
                    $"a constant {fnName} is not one a split rule may hold: Table 'Add' belongs to another graph"),
                ("a vector of another graph's", () => Define("a vector of another graph's", hv, Vec.Of(foreign)),
                    $"a constant {vecName} is not one a split rule may hold: Table 'Add' belongs to another graph"),
            };
            foreach (var (name, define, message) in cases)
            {
                var error = Assert.Throws<RuleDefinitionException>(define);
                Assert.Equal(name, error.RuleName);
                Assert.Equal(message, error.Message);
            }

            Assert.Equal("pick", Assert.Single(graph.SplitRules).Name);
        }

        /// <summary>
        /// A split's cases make a vector of a class of the scope's in an in-case rule, each in its own containers under
        /// an id local to the case, and write it as a function's value beside a relation's row: the lift carries the
        /// relation's row, which every case proves, to the scope, and drops the function's row, whose value no id of
        /// the scope stands for, as it drops a row naming a class a case made (decisions row 9). egglog has no split,
        /// so the expectation is what the lift does with a case-local id; the scope's own run of the rule writes both
        /// rows.
        /// </summary>
        [Fact]
        public void TheLiftDropsARowNamingAVectorACaseMade()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            var g = graph.DefineFunction<long, Vec<Node>>("g");
            var done = graph.DefineRelation<long>("done");
            var choice = graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var split = graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<long>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)));
            });
            var make = graph.DefineRuleset("make");
            make.DefineRule("make", r =>
            {
                var x = r.Var<Node>("x");
                r.When(x.Is(num.Of(1L)));
                r.Then(g.Of(1L).Is(Primitives.VecOf<Node>().Of(x)), done.Of(1L));
            });
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            root.Assume(choice.Of("k").Is(Set.Of(1L, 2L)));
            var vectors = graph.Engine.ContainerValues.GetEnvironment<Relations.VecContainer>();

            root.Run(Schedule.Split(split, Schedule.Run(make)));

            Assert.False(root.IsFaulted);
            Assert.Empty(root.Children);
            Assert.True(root.Check(done.Of(1L)));
            Assert.False(root.TryGet(g.Of(1L), out _));
            Assert.Equal(0, vectors.Count);
            root.Engine.Context.CheckInvariants();

            root.Run(Schedule.Run(make));
            Assert.Equal(Vec.Of(one), root.Get(g.Of(1L)));
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
        [InlineData(true, true)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        public void AFailedFlushInAChildsBatchKeepsTheVectorsItMergedAndTheBatchGoesOn(bool frozen, bool held)
        {
            // A host batch in the child unions (A) and (B), which the vectors hold (held), so that its container pass
            // merges (vec-of (A)) and (vec-of (B)); or which makes (K (A)) and (K (B)) one by congruence, so that the
            // pass merges (vec-of (K (A))) and (vec-of (K (B))). The rows of f keyed by them meet and f's lattice
            // throws. The batch's next call flushes first and throws the recorded failure; the batch goes on, its later
            // write landing on the merged vector, and the child is not faulted, as a root's batch running the same
            // calls is not, which gives the expected values (egglog has no lattice that throws).
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var k = graph.DefineConstructor<Node, Node>("K");
            graph.Values.RegisterVec<Node>();
            var boom = new ValueLattice<long>("boom", static (x, y) => (x, y) is (1L, 2L) or (2L, 1L) ? throw new InvalidOperationException("boom") : Math.Max(x, y));
            var f = graph.DefineFunction<Vec<Node>, long>("f", boom);
            var h = graph.DefineFunction<Vec<Node>, long>("h", ValueLattice.Max<long>());
            var va = Primitives.VecOf<Node>().Of(held ? a.Of() : k.Of(a.Of()));
            var vb = Primitives.VecOf<Node>().Of(held ? b.Of() : k.Of(b.Of()));
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
                Assert.True(child.AreEqual(child.Make(a.Of()), child.Make(b.Of())));
                Assert.Equal(held == false, child.Check(k.Of(a.Of()).Is(k.Of(b.Of()))));
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

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DisposingAChildThatMergedHeldClassesRestoresTheParentExactly(bool frozen)
        {
            // Probes pop/: egglog at 5e655cae serializes with.egg, whose push merges classes containers hold with a
            // host union, a multiset-union-values and a rule's vec-union, so that (check (= (Num 1) (Num 4))), (check
            // (= (h (f)) 2)) and (check (= (f) (v3))) hold there and L and h have one row each, to the same JSON as
            // without.egg, the program without the push: its pop restores the root exactly. Here the child does the
            // same, and once it is disposed the root serializes as before the child was made, holds as many containers,
            // and a child made then sees the root's.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            graph.Values.RegisterVec<Node>();
            graph.Values.RegisterSet<Node>();
            graph.Values.RegisterMultiSet<Node>();
            var l = graph.DefineConstructor<Vec<Node>, Node>("L");
            var f = graph.DefineFunction<Vec<Node>>("f");
            var v3 = graph.DefineFunction<Vec<Node>>("v3");
            var s = graph.DefineFunction<Set<Node>>("s");
            var ms = graph.DefineFunction<MultiSet<Node>>("ms");
            var h = graph.DefineFunction<Vec<Node>, long>("h", ValueLattice.Max<long>());
            var of = Primitives.VecOf<Node>();
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("union", r =>
            {
                var (v, w) = r.Vars<Vec<Node>, Vec<Node>>("v", "w");
                r.When(v.Is(f.Of()), w.Is(v3.Of()));
                r.Then(Fact.Exists(Primitives.VecUnion<Node>().Of(v, w)));
            });
            var root = graph.Root;
            var one = root.Make(num.Of(1L));
            root.Assume(f.Of().Is(of.Of(num.Of(2L))), v3.Of().Is(of.Of(num.Of(3L))));
            root.Assume(s.Of().Is(Primitives.SetOf<Node>().Of(num.Of(1L), num.Of(2L), num.Of(3L))));
            root.Assume(ms.Of().Is(Primitives.MultiSetOf<Node>().Of(num.Of(3L), num.Of(4L))));
            root.Make(l.Of(of.Of(num.Of(2L))));
            root.Make(l.Of(of.Of(num.Of(1L))));
            root.Assume(h.Of(of.Of(num.Of(1L))).Is(1L), h.Of(of.Of(num.Of(2L))).Is(2L));
            if (frozen)
                root.Freeze();

            var before = root.Serialize().ToJson();
            var interned = graph.Values.InternedCount;
            using (var child = root.CreateChild())
            {
                child.Union(one, child.Make(num.Of(2L)));
                child.Assume(Fact.Exists(Primitives.MultiSetUnionValues<Node>().Of(ms.Of())));
                child.Run(rules);
                Assert.False(child.IsFaulted);
                Assert.True(child.AreEqual(one, child.Make(num.Of(4L))));
                Assert.Equal(2L, child.Get(h.Of(f.Of())));
                Assert.True(child.Check(Primitives.Count<Node>().Of(s.Of()).Is(1L)));
                Assert.True(child.Check(f.Of().Is(v3.Of())));
                Assert.Equal(1, child.CountRows(l));
                Assert.Equal(1, child.CountRows(h));
                child.Engine.Context.CheckInvariants();
            }

            Assert.Equal(before, root.Serialize().ToJson());
            Assert.Equal(interned, graph.Values.InternedCount);
            root.Engine.Context.CheckInvariants();
            using var later = root.CreateChild();
            Assert.False(later.Check(f.Of().Is(v3.Of())));
            Assert.Equal(2, later.CountRows(l));
            Assert.Equal(2, later.CountRows(h));
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
