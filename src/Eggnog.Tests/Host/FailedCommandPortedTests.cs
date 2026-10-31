using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Host
{

    /// <summary>
    /// What a failed command keeps, as egglog's: its actions run as a rule (src/lib.rs:1171-1195), whose writes staged
    /// before the panic outlive it, so the terms its calls made and what its primitives wrote stand, while its own write
    /// does not happen, or is refused where it conflicts; a write its flush refuses keeps the value before it, and the
    /// command's other writes stand, its own included. Each program ran on egglog's binary at 5e655cae as the comments
    /// say; those with write primitives ran on egglog's library at 5e655cae, with full primitives (setf k v) staging
    /// (f k) = v and returning k, (uni a b) unioning (Num a) and (Num b) and returning a, and (setf-boom k v) staging
    /// (f k) = v with no result.
    /// </summary>
    public sealed class FailedCommandPortedTests
    {

        sealed class Theory
        {

            public Theory()
            {
                Graph.DefineSort<Node>("Math");
                Num = Graph.DefineConstructor<long, Node>("Num");
                Lit = Graph.DefineConstructor<long, Node>("Lit");
                Add = Graph.DefineConstructor<Node, Node, Node>("Add");
                H = Graph.DefineFunction<Node, long>("h");
                Graph.Values.RegisterVec<long>();
            }

            public EGraph Graph { get; } = new();

            public Scope Root => Graph.Root;

            public Constructor<long, Node> Num { get; }

            public Constructor<long, Node> Lit { get; }

            public Constructor<Node, Node, Node> Add { get; }

            public Function<Node, long> H { get; }

            /// <summary>
            /// <c>(function f (i64) i64 :no-merge)</c>, which <see cref="SetF"/> writes.
            /// </summary>
            public Function<long, long> F => f ??= Graph.DefineFunction<long, long>("f");

            /// <summary>
            /// <c>(function g (i64) i64 :no-merge)</c>.
            /// </summary>
            public Function<long, long> G => g ??= Graph.DefineFunction<long, long>("g");

            /// <summary>
            /// <c>(setf k v)</c>: writes (f k) = v and returns k.
            /// </summary>
            public PrimitiveN<long> SetF => setF ??= Primitive.CreateWrite("setf", [typeof(long), typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Set(F, [args[0]], args[1]);
                result = (long)args[0];
                return true;
            });

            /// <summary>
            /// <c>(setf-boom k v)</c>: writes (f k) = v, and has no result.
            /// </summary>
            public PrimitiveN<long> SetFBoom => setFBoom ??= Primitive.CreateWrite("setf-boom", [typeof(long), typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Set(F, [args[0]], args[1]);
                result = 0;
                return false;
            });

            Function<long, long>? f;
            Function<long, long>? g;
            PrimitiveN<long>? setF;
            PrimitiveN<long>? setFBoom;

            /// <summary>
            /// Gets the scope a test writes: the root, or a child of the root, frozen, as egglog's push.
            /// </summary>
            public Scope Open(bool inChild)
            {
                _ = F;
                _ = G;
                if (inChild == false)
                    return Root;

                Root.Freeze();
                return Root.CreateChild();
            }

            /// <summary>
            /// <c>(vec-get (vec-of 1) 5)</c>, which has no result.
            /// </summary>
            public static Term<long> Missing => Primitives.VecGet<long>().Of(Primitives.VecOf<long>().Of(1L), 5L);

            public bool Has(Term<Node> term) => Root.Check(Fact.Exists(term));

        }

        [Fact]
        public void TheTermsAFailedCommandMadeStand()
        {
            // (fail (Add (Num 30) (Lit (vec-get (vec-of 1) 5)))) (check (Num 30)) (fail (check (Add (Num 30) (Lit 0))))
            var t = new Theory();
            Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(Fact.Exists(t.Add.Of(t.Num.Of(30L), t.Lit.Of(Theory.Missing)))));
            Assert.True(t.Has(t.Num.Of(30L)));
            Assert.False(t.Root.IsFaulted);

            // (fail (set (h (Num 40)) (vec-get (vec-of 1) 5))) (check (Num 40)) (fail (check (h (Num 40))))
            Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(t.H.Of(t.Num.Of(40L)).Is(Theory.Missing)));
            Assert.True(t.Has(t.Num.Of(40L)));
            Assert.False(t.Root.Check(Fact.Exists(t.H.Of(t.Num.Of(40L)))));

            // (fail (union (Num 8) (Lit (vec-get (vec-of 1) 5)))) (check (Num 8))
            Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(t.Num.Of(8L).Is(t.Lit.Of(Theory.Missing))));
            Assert.True(t.Has(t.Num.Of(8L)));

            // The same through Make: (fail (Add (Num 31) (Lit (vec-get (vec-of 1) 5)))) leaves (Num 31).
            Assert.Throws<RuleEvaluationException>(() => t.Root.Make(t.Add.Of(t.Num.Of(31L), t.Lit.Of(Theory.Missing))));
            Assert.True(t.Has(t.Num.Of(31L)));
        }

        [Fact]
        public void AConflictingWriteKeepsTheTermsItsKeysMade()
        {
            // (set (h (Num 51)) 1) (fail (set (h (Num 51)) 2)) (check (= (h (Num 51)) 1))
            // (fail (set (h (Num 51)) (h (Num 53)))) (check (Num 53))
            var t = new Theory();
            t.Root.Assume(t.H.Of(t.Num.Of(51L)).Is(1L));
            Assert.Throws<FactConflictException>(() => t.Root.Assume(t.H.Of(t.Num.Of(51L)).Is(2L)));
            Assert.Equal(1L, t.Root.Get(t.H.Of(t.Num.Of(51L))));
            Assert.ThrowsAny<Exception>(() => t.Root.Assume(t.H.Of(t.Num.Of(51L)).Is(t.H.Of(t.Num.Of(53L)))));
            Assert.True(t.Has(t.Num.Of(53L)));

            // (function g (i64) Math :no-merge) (set (g 1) (Num 58)) (fail (set (g 1) (Num 57))) (check (Num 57))
            // (check (= (g 1) (Num 58))): the conflicting write's value term stands, the write does not.
            var g = t.Graph.DefineFunction<long, Node>("g");
            t.Root.Assume(g.Of(1L).Is(t.Num.Of(58L)));
            Assert.Throws<FactConflictException>(() => t.Root.Assume(g.Of(1L).Is(t.Num.Of(57L))));
            Assert.True(t.Has(t.Num.Of(57L)));
            Assert.True(t.Root.Check(g.Of(1L).Is(t.Num.Of(58L))));
        }

        [Fact]
        public void ABatchStopsAtAFailedFact()
        {
            // (Num 60) (Add (Num 61) (Lit (vec-get (vec-of 1) 5))) (Num 62) as a program: egglog stops at the error, after
            // the first command and what the second made before it failed.
            var t = new Theory();
            Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(
                Fact.Exists(t.Num.Of(60L)),
                Fact.Exists(t.Add.Of(t.Num.Of(61L), t.Lit.Of(Theory.Missing))),
                Fact.Exists(t.Num.Of(62L))));
            Assert.True(t.Has(t.Num.Of(60L)));
            Assert.True(t.Has(t.Num.Of(61L)));
            Assert.False(t.Has(t.Num.Of(62L)));
        }

        [Fact]
        public void WhatAFailedCommandsPrimitivesWroteStands()
        {
            // (let $p (vec-of (Num 1))) (let $q (vec-of (Num 2)))
            // (fail (Add (vec-get (vec-union $p $q) 0) (Lit (vec-get (vec-of 1) 5)))): egglog stages the union before the
            // panic and returns before its rebuild, so it shows only after a later union rebuilds (the egglog engine map's
            // defect 22); the port rebuilds what a failed command staged, so the union stands.
            var t = new Theory();
            t.Graph.Values.RegisterVec<Node>();
            var of = Primitives.VecOf<Node>();
            var unioned = Primitives.VecGet<Node>().Of(Primitives.VecUnion<Node>().Of(of.Of(t.Num.Of(1L)), of.Of(t.Num.Of(2L))), 0L);
            Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(Fact.Exists(t.Add.Of(unioned, t.Lit.Of(Theory.Missing)))));
            Assert.True(t.Root.Check(t.Num.Of(1L).Is(t.Num.Of(2L))));

            // A write primitive that inserts a row, then has no result: the row stands, as egglog's staged insert does.
            var marked = t.Graph.DefineRelation<long>("marked");
            var mark = Primitive.CreateWrite("mark", [typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Insert(marked, args[0]);
                result = 0;
                return false;
            });
            Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(Fact.Exists(mark.Of(5L))));
            Assert.True(t.Root.Check(marked.Of(5L)));
        }

        [Fact]
        public void ARefusedFactWritesNothing()
        {
            // A fact egglog's type checker refuses never runs: a read of a scoped view in a write, a fact that is a filter.
            var t = new Theory();
            Assert.Throws<ArgumentException>(() => t.Root.Assume(Fact.True(true)));
            Assert.Throws<ArgumentException>(() => t.Root.Assume(((Term<long>)1L).Is(2L)));
            Assert.Equal(0, t.Root.CountRows(t.Num));
        }

        [Fact]
        public void AConflictingConstructorWriteGoesWithItsCongruence()
        {
            // (set (h x) 1) (set (h y) 2), y = (Wrap (Num 2)), then (union (Num 1) (Num 2)) and (union (Wrap (Num 1)) x) in
            // one call: the second makes (Wrap (Num 1)) meet y, so h's rows of x and y meet with two values. The fact is
            // refused, its constructor's output set to x with it; the union before it stands, and nothing faults.
            var t = new Theory();
            var wrap = t.Graph.DefineConstructor<Node, Node>("Wrap");
            var y = t.Root.Make(wrap.Of(t.Num.Of(2L)));
            var x = t.Root.Make(t.Lit.Of(1L));
            t.Root.Make(t.Num.Of(1L));
            t.Root.Assume(t.H.Of(x).Is(1L), t.H.Of(y).Is(2L));
            Assert.Throws<FactConflictException>(() => t.Root.Assume(t.Num.Of(1L).Is(t.Num.Of(2L)), wrap.Of(t.Num.Of(1L)).Is(x)));
            Assert.False(t.Root.IsFaulted);
            Assert.True(t.Root.Check(t.Num.Of(1L).Is(t.Num.Of(2L))));
            Assert.False(t.Root.AreEqual(x, y));
            Assert.Equal(1L, t.Root.Get(t.H.Of(x)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ADeletionAPrimitiveHeldStandsWhenItsWriteConflicts(bool inChild)
        {
            // (set (k 0) 1) (C 1); a write primitive (delc-setk a) deletes (C a), stages (k 0) = 2 and returns a:
            // (fail (subsume (C (delc-setk 1)))), "Illegal merge attempted for function k", leaves (C 1) subsumed, as the
            // flush deletes it before it inserts the subsumed row, and (k 0) at 1.
            var t = new Theory();
            var c = t.Graph.DefineConstructor<long, Node>("C");
            var k = t.Graph.DefineFunction<long, long>("k");
            var p = Primitive.CreateWrite("delc-setk", [typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Remove(c, args[0]);
                tables.Set(k, [0L], 2L);
                result = (long)args[0];
                return true;
            });
            var s = t.Open(inChild);
            s.Assume(k.Of(0L).Is(1L));
            Assert.Throws<FactConflictException>(() => s.Write(w =>
            {
                w.Make(c.Of(1L));
                w.Subsume(c.Of(p.Of(1L)));
            }));
            Assert.True(Assert.Single(s.Rows(c)).Subsumed);
            Assert.Equal(1L, s.Get(k.Of(0L)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AHeldWriteThatConflictsKeepsTheFactsOwnWrite(bool inChild)
        {
            // (set (f 1) 5) (fail (set (g 1) (setf 1 9))), "Illegal merge attempted for function f": (g 1) is 1 and (f 1)
            // stays 5. (fail (r (setf 1 9))) leaves (r 1).
            var t = new Theory();
            var r = t.Graph.DefineRelation<long>("r");
            var s = t.Open(inChild);
            s.Assume(t.F.Of(1L).Is(5L));
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(1L).Is(t.SetF.Of(1L, 9L))));
            Assert.Equal(1L, s.Get(t.G.Of(1L)));
            Assert.Equal(5L, s.Get(t.F.Of(1L)));
            Assert.Throws<FactConflictException>(() => s.Assume(r.Of(t.SetF.Of(1L, 9L))));
            Assert.True(s.Check(r.Of(1L)));
            Assert.Equal(5L, s.Get(t.F.Of(1L)));
            Assert.False(s.IsFaulted);

            // (set (f 4) 0) (Num 3) (Num 4) (fail (union (Num 3) (Num (setf 4 7)))): the union is staged, and stands once
            // a rebuild runs, which egglog's failed command skips (the egglog engine map's defect 22) and the port makes.
            s.Assume(t.F.Of(4L).Is(0L), Fact.Exists(t.Num.Of(3L)), Fact.Exists(t.Num.Of(4L)));
            Assert.Throws<FactConflictException>(() => s.Assume(t.Num.Of(3L).Is(t.Num.Of(t.SetF.Of(4L, 7L)))));
            Assert.True(s.Check(t.Num.Of(3L).Is(t.Num.Of(4L))));
            Assert.Equal(0L, s.Get(t.F.Of(4L)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AHeldWriteGoesBeforeTheFactsOwnWrite(bool inChild)
        {
            // (fail (set (f 6) (setf 6 2))): the primitive stages (f 6) = 2 before the command stages (f 6) = 6, so the
            // flush keeps 2. (fail (set (g 8) (setf 8 (setf 8 1)))): the inner call stages first, (f 8) is 1, (g 8) 8.
            var t = new Theory();
            var s = t.Open(inChild);
            Assert.Throws<FactConflictException>(() => s.Assume(t.F.Of(6L).Is(t.SetF.Of(6L, 2L))));
            Assert.Equal(2L, s.Get(t.F.Of(6L)));
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(8L).Is(t.SetF.Of(8L, t.SetF.Of(8L, 1L)))));
            Assert.Equal(1L, s.Get(t.F.Of(8L)));
            Assert.Equal(8L, s.Get(t.G.Of(8L)));

            // (set (f 7) 1) (fail (set (f 7) (setg 7 3))): the fact's own write conflicts, and what its primitive wrote
            // stands.
            var setG = Primitive.CreateWrite("setg", [typeof(long), typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Set(t.G, [args[0]], args[1]);
                result = (long)args[0];
                return true;
            });
            s.Assume(t.F.Of(7L).Is(1L));
            Assert.Throws<FactConflictException>(() => s.Assume(t.F.Of(7L).Is(setG.Of(7L, 3L))));
            Assert.Equal(1L, s.Get(t.F.Of(7L)));
            Assert.Equal(3L, s.Get(t.G.Of(7L)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ANestedHeldWriteThatConflictsKeepsTheOthers(bool inChild)
        {
            // (set (f 5) 0) (fail (set (g 12) (setf 12 (setf 5 1)))): (f 5) stays 0, (f 12) is 5 and (g 12) 12.
            // (set (f 13) 0) (fail (set (g 13) (setf 13 (setf 14 1)))): (f 13) stays 0, (f 14) is 1 and (g 13) 13.
            var t = new Theory();
            var s = t.Open(inChild);
            s.Assume(t.F.Of(5L).Is(0L), t.F.Of(13L).Is(0L));
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(12L).Is(t.SetF.Of(12L, t.SetF.Of(5L, 1L)))));
            Assert.Equal(0L, s.Get(t.F.Of(5L)));
            Assert.Equal(5L, s.Get(t.F.Of(12L)));
            Assert.Equal(12L, s.Get(t.G.Of(12L)));
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(13L).Is(t.SetF.Of(13L, t.SetF.Of(14L, 1L)))));
            Assert.Equal(0L, s.Get(t.F.Of(13L)));
            Assert.Equal(1L, s.Get(t.F.Of(14L)));
            Assert.Equal(13L, s.Get(t.G.Of(13L)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void APrimitiveThatFailsAfterAnEarlierOneWroteKeepsItsWrite(bool inChild)
        {
            // (fail (set (g 9) (boom (setf 9 1)))), (fail (set (g 10) (setf-boom 10 1))) and
            // (fail (set (g 11) (+ (setf 11 1) (boom 0)))), boom a primitive with no result: (f 9), (f 10) and (f 11)
            // are 1, and no (g 9), (g 10) or (g 11).
            var t = new Theory();
            var s = t.Open(inChild);
            var add = Primitives.Add;
            Assert.Throws<RuleEvaluationException>(() => s.Assume(t.G.Of(9L).Is(Primitives.VecGet<long>().Of(Primitives.VecOf<long>().Of(t.SetF.Of(9L, 1L)), 5L))));
            Assert.Throws<RuleEvaluationException>(() => s.Assume(t.G.Of(10L).Is(t.SetFBoom.Of(10L, 1L))));
            Assert.Throws<RuleEvaluationException>(() => s.Assume(t.G.Of(11L).Is(add.Of(t.SetF.Of(11L, 1L), Theory.Missing))));
            foreach (var key in new[] { 9L, 10L, 11L })
            {
                Assert.Equal(1L, s.Get(t.F.Of(key)));
                Assert.False(s.Check(Fact.Exists(t.G.Of(key))));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AHeldUnionThatConflictsKeepsTheFactsOwnWrite(bool inChild)
        {
            // (set (h (Num 1)) 1) (set (h (Num 2)) 2) (fail (set (g 3) (uni 1 2))), "Illegal merge attempted for function
            // h": (g 3) is 1. egglog keeps the union too and one of h's rows; the batch refuses a union that conflicts, as
            // it refuses the fact's own (architecture 2.3), and keeps the rest of the fact.
            var t = new Theory();
            var s = t.Open(inChild);
            var one = s.Make(t.Num.Of(1L));
            var two = s.Make(t.Num.Of(2L));
            var uni = Primitive.CreateWrite("uni", [typeof(long), typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Union(one, two);
                result = (long)args[0];
                return true;
            });
            s.Assume(t.H.Of(one).Is(1L), t.H.Of(two).Is(2L));
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(3L).Is(uni.Of(1L, 2L))));
            Assert.False(s.IsFaulted);
            Assert.Equal(1L, s.Get(t.G.Of(3L)));
            Assert.False(s.AreEqual(one, two));
            Assert.Equal(2L, s.Get(t.H.Of(two)));

            // (function q (Math) Math :no-merge) (set (q (Num 1)) (Num 10)) (set (q (Num 2)) (Num 20))
            // (fail (set (g 30) (uni 1 2))), "Illegal merge attempted for function q", which the rebuild's passes find:
            // (g 30) is 1.
            var q = t.Graph.DefineFunction<Node, Node>("q");
            s.Assume(q.Of(one).Is(t.Num.Of(10L)), q.Of(two).Is(t.Num.Of(20L)));
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(30L).Is(uni.Of(1L, 2L))));
            Assert.Equal(1L, s.Get(t.G.Of(30L)));
            Assert.False(s.AreEqual(one, two));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFactAfterAFailedOneStands(bool inChild)
        {
            // (set (f 1) 5) (set (g 1) (setf 1 9)) (set (g 2) 2) as a program stops at the failure, after (g 1); a Write
            // callback that catches it goes on, as the next command.
            var t = new Theory();
            var s = t.Open(inChild);
            s.Assume(t.F.Of(1L).Is(5L));
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(1L).Is(t.SetF.Of(1L, 9L)), t.G.Of(2L).Is(2L)));
            Assert.Equal(1L, s.Get(t.G.Of(1L)));
            Assert.False(s.Check(Fact.Exists(t.G.Of(2L))));
            s.Write(w =>
            {
                Assert.Throws<FactConflictException>(() => w.Assume(t.G.Of(3L).Is(t.SetF.Of(1L, 8L))));
                w.Assume(t.G.Of(4L).Is(t.F.Of(1L)));
            });
            Assert.Equal(1L, s.Get(t.G.Of(3L)));
            Assert.Equal(5L, s.Get(t.G.Of(4L)));
            Assert.Equal(5L, s.Get(t.F.Of(1L)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ADeletionGoesBeforeTheWritesItsPrimitivesMade(bool inChild)
        {
            // (set (f 21) 1) (delete (f (setf 21 2))): the flush applies the command's deletion before its primitive's
            // insert, so the command succeeds and (f 21) is 2. With (insr k) inserting (r k) and returning k:
            // (r 1) (delete (r (insr 1))) leaves (r 1), and (delete (r (insr 2))) makes (r 2).
            var t = new Theory();
            var r = t.Graph.DefineRelation<long>("r");
            var insr = Primitive.CreateWrite("insr", [typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Insert(r, args[0]);
                result = (long)args[0];
                return true;
            });
            var s = t.Open(inChild);
            s.Assume(t.F.Of(21L).Is(1L), r.Of(1L));
            s.Delete(t.F.Of(t.SetF.Of(21L, 2L)));
            Assert.Equal(2L, s.Get(t.F.Of(21L)));
            s.Delete(r.Of(insr.Of(1L)));
            Assert.True(s.Check(r.Of(1L)));
            s.Delete(r.Of(insr.Of(2L)));
            Assert.True(s.Check(r.Of(2L)));
            Assert.False(s.IsFaulted);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void AHeldUnionThatConflictsKeepsTheOtherHeldUnions(bool inChild, bool conflictFirst)
        {
            // (set (h (Num 1)) 1) (set (h (Num 2)) 2) (Num 5) (Num 6) (fail (set (g 40) (+ (uni 1 2) (uni 5 6)))), and with
            // the two calls swapped: "Illegal merge attempted for function h", (g 40) is 6, and (Num 5) and (Num 6) are
            // one, whatever the order. egglog keeps the union of 1 and 2 as well; the batch refuses it (architecture
            // 2.13, item 7). The same through (function q (Math) Math :no-merge), whose conflict the rebuild's passes find.
            var t = new Theory();
            var q = t.Graph.DefineFunction<Node, Node>("q");
            var s = t.Open(inChild);
            var classes = new System.Collections.Generic.Dictionary<long, Node>();
            foreach (var key in new[] { 1L, 2L, 5L, 6L, 11L, 12L, 15L, 16L })
                classes[key] = s.Make(t.Num.Of(key));

            var uni = Primitive.CreateWrite("uni", [typeof(long), typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Union(classes[(long)args[0]], classes[(long)args[1]]);
                result = (long)args[0];
                return true;
            });
            var add = Primitives.Add;
            s.Assume(t.H.Of(classes[1]).Is(1L), t.H.Of(classes[2]).Is(2L));
            s.Assume(q.Of(classes[11]).Is(t.Num.Of(100L)), q.Of(classes[12]).Is(t.Num.Of(200L)));
            var conflicting = uni.Of(1L, 2L);
            var other = uni.Of(5L, 6L);
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(40L).Is(conflictFirst ? add.Of(conflicting, other) : add.Of(other, conflicting))));
            Assert.Equal(6L, s.Get(t.G.Of(40L)));
            Assert.True(s.AreEqual(classes[5], classes[6]));
            Assert.False(s.AreEqual(classes[1], classes[2]));

            conflicting = uni.Of(11L, 12L);
            other = uni.Of(15L, 16L);
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(41L).Is(conflictFirst ? add.Of(conflicting, other) : add.Of(other, conflicting))));
            Assert.Equal(26L, s.Get(t.G.Of(41L)));
            Assert.True(s.AreEqual(classes[15], classes[16]));
            Assert.False(s.AreEqual(classes[11], classes[12]));
            Assert.False(s.IsFaulted);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AHeldUnionThatConflictsAmongManyIsRefusedAlone(bool inChild)
        {
            // One call that unions two classes whose rows of q conflict, then 500 pairs of classes with no rows, over a q
            // of 2000 rows: the fact fails, refusing the first union only, and every pair is one, in a few checks of the
            // rows rather than one for each union.
            const int Pairs = 500;
            var t = new Theory();
            var q = t.Graph.DefineFunction<Node, Node>("q");
            var s = t.Open(inChild);
            var keyed = new Node[2000];
            s.Write(w =>
            {
                for (var i = 0; i < keyed.Length; i++)
                {
                    keyed[i] = w.Make(t.Num.Of((long)i));
                    w.Assume(q.Of(keyed[i]).Is(t.Num.Of(10000L + i)));
                }
            });
            var free = new Node[2 * Pairs];
            s.Write(w =>
            {
                for (var i = 0; i < free.Length; i++)
                    free[i] = w.Make(t.Num.Of(20000L + i));
            });
            var many = Primitive.CreateWrite("uni-many", [typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Union(keyed[0], keyed[1]);
                for (var i = 0; i < Pairs; i++)
                    tables.Union(free[2 * i], free[2 * i + 1]);

                result = (long)args[0];
                return true;
            });
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(50L).Is(many.Of(7L))));
            Assert.Equal(7L, s.Get(t.G.Of(50L)));
            Assert.False(s.AreEqual(keyed[0], keyed[1]));
            for (var i = 0; i < Pairs; i++)
                Assert.True(s.AreEqual(free[2 * i], free[2 * i + 1]));
        }

        [Fact]
        public void AHeldWriteFoldsBeforeTheFactsOwnWrite()
        {
            // (function n (i64) i64 :merge (- old new)), (setn k v) staging (n k) = v and returning k:
            // (set (n 1) (setn 1 10)) makes (n 1) 9, the primitive's 10 folded first, then the command's 1;
            // (set (n 2) 100) (set (n 2) (setn 2 10)) makes (n 2) 88. A merge only the root writes, so at the root.
            var t = new Theory();
            var n = t.Graph.DefineFunction<long, long>("n", FunctionMerge.Of<long>("minus", (old, value) => old - value));
            var setn = Primitive.CreateWrite("setn", [typeof(long), typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Set(n, [args[0]], args[1]);
                result = (long)args[0];
                return true;
            });
            t.Root.Assume(n.Of(1L).Is(setn.Of(1L, 10L)));
            Assert.Equal(9L, t.Root.Get(n.Of(1L)));
            t.Root.Assume(n.Of(2L).Is(100L));
            t.Root.Assume(n.Of(2L).Is(setn.Of(2L, 10L)));
            Assert.Equal(88L, t.Root.Get(n.Of(2L)));
        }

        [Fact]
        public void ARefusalAfterATermWritesNothing()
        {
            // A class of another graph is refused as egglog's type checker refuses a command before it runs: the term
            // planned before it, (Num 72), goes too.
            var t = new Theory();
            var other = new EGraph();
            other.DefineSort<Node>("Math");
            var foreign = other.Root.Make(other.DefineConstructor<long, Node>("Num").Of(1L));
            Assert.Throws<ArgumentException>(() => t.Root.Assume(Fact.Exists(t.Add.Of(t.Num.Of(72L), foreign))));
            Assert.False(t.Has(t.Num.Of(72L)));
        }
    }

}
