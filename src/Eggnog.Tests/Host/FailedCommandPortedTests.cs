using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Host
{

    /// <summary>
    /// What a failed command keeps, as egglog's: its actions run as a rule (src/lib.rs:1171-1195), whose writes staged
    /// before the panic outlive it, so the terms its calls made and what its primitives wrote stand, while its own write
    /// does not happen, or is refused where it conflicts; a write its flush refuses keeps the value before it, and the
    /// command's other writes stand, its own included; a union whose rebuild makes two rows of a function without a merge
    /// meet with different values stands, with one of the rows, and so does the rest. Each program ran on egglog's binary
    /// at 5e655cae as the comments
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AConflictingConstructorWriteGoesWithItsCongruence(bool inChild)
        {
            // (Wrap (Num 2)) (Lit 1) (Num 1) (set (h (Lit 1)) 1) (set (h (Wrap (Num 2))) 2) (union (Num 1) (Num 2))
            // (union (Wrap (Num 1)) (Lit 1)), the last two in one call: the second makes (Wrap (Num 1)) meet
            // (Wrap (Num 2)), so h's two rows meet, "Illegal merge attempted for function h". The union stands, with its
            // congruence, and the rebuild keeps the row of (Wrap (Num 2)), whose id is the smaller: (h (Lit 1)) is 2.
            var t = new Theory();
            var wrap = t.Graph.DefineConstructor<Node, Node>("Wrap");
            var s = t.Open(inChild);
            var y = s.Make(wrap.Of(t.Num.Of(2L)));
            var x = s.Make(t.Lit.Of(1L));
            s.Make(t.Num.Of(1L));
            s.Assume(t.H.Of(x).Is(1L), t.H.Of(y).Is(2L));
            var e = Assert.Throws<FactConflictException>(() => s.Assume(t.Num.Of(1L).Is(t.Num.Of(2L)), wrap.Of(t.Num.Of(1L)).Is(x)));
            Assert.Equal(2L, e.StoredValue);
            Assert.Equal(1L, e.NewValue);
            Assert.False(s.IsFaulted);
            Assert.True(s.Check(t.Num.Of(1L).Is(t.Num.Of(2L))));
            Assert.True(s.AreEqual(x, y));
            Assert.Equal(2L, s.Get(t.H.Of(x)));
            Assert.Equal(1, s.CountRows(t.H));
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
            var add = Int64Primitives.Add;
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
        public void AHeldUnionThatConflictsStandsWithTheFactsOwnWrite(bool inChild)
        {
            // (set (h (Num 1)) 1) (set (h (Num 2)) 2) (fail (set (g 3) (uni 1 2))), "Illegal merge attempted for function
            // h": (g 3) is 1, the union stands, (check (= (Num 1) (Num 2))), and the rebuild keeps (h (Num 1)), 1; a later
            // (set (h (Num 2)) 2) fails, and (set (g 4) (h (Num 2))) makes (g 4) 1.
            var t = new Theory();
            var q = t.Graph.DefineFunction<Node, Node>("q");
            var s = t.Open(inChild);
            var classes = new System.Collections.Generic.Dictionary<long, Node>();
            foreach (var key in new[] { 1L, 2L, 3L, 4L })
                classes[key] = s.Make(t.Num.Of(key));

            var uni = Primitive.CreateWrite("uni", [typeof(long), typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Union(classes[(long)args[0]], classes[(long)args[1]]);
                result = (long)args[0];
                return true;
            });
            s.Assume(t.H.Of(classes[1]).Is(1L), t.H.Of(classes[2]).Is(2L));
            var e = Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(3L).Is(uni.Of(1L, 2L))));
            Assert.Same(t.H, e.Table);
            Assert.Equal(1L, e.StoredValue);
            Assert.False(s.IsFaulted);
            Assert.Equal(1L, s.Get(t.G.Of(3L)));
            Assert.True(s.AreEqual(classes[1], classes[2]));
            Assert.Equal(1L, s.Get(t.H.Of(classes[2])));
            Assert.Equal(1, s.CountRows(t.H));
            Assert.Throws<FactConflictException>(() => s.Assume(t.H.Of(classes[2]).Is(2L)));
            s.Assume(t.G.Of(4L).Is(t.H.Of(classes[2])));
            Assert.Equal(1L, s.Get(t.G.Of(4L)));

            // (function q (Math) Math :no-merge) (set (q (Num 3)) (Num 10)) (set (q (Num 4)) (Num 20))
            // (fail (set (g 30) (uni 3 4))), "Illegal merge attempted for function q", which the rebuild's passes find:
            // (g 30) is 3, the union stands, q keeps (Num 10), and (Num 10) and (Num 20) stay apart.
            s.Assume(q.Of(classes[3]).Is(t.Num.Of(10L)), q.Of(classes[4]).Is(t.Num.Of(20L)));
            e = Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(30L).Is(uni.Of(3L, 4L))));
            Assert.Same(q, e.Table);
            Assert.Equal(3L, s.Get(t.G.Of(30L)));
            Assert.True(s.AreEqual(classes[3], classes[4]));
            Assert.True(s.Check(q.Of(classes[4]).Is(t.Num.Of(10L))));
            Assert.False(s.Check(t.Num.Of(10L).Is(t.Num.Of(20L))));
            Assert.Equal(1, s.CountRows(q));
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
        public void AHeldUnionThatConflictsStandsWithTheOtherHeldUnions(bool inChild, bool conflictFirst)
        {
            // (Num 1) (Num 2) (Num 5) (Num 6) (Num 11) (Num 12) (Num 15) (Num 16) (set (h (Num 1)) 1) (set (h (Num 2)) 2)
            // (fail (set (g 40) (+ (uni 1 2) (uni 5 6)))), and with the two calls swapped: "Illegal merge attempted for
            // function h", (g 40) is 6, both unions stand, whatever the order, and h keeps (h (Num 1)), 1. The same
            // through (function q (Math) Math :no-merge), whose conflict the rebuild's passes find, with (set (q (Num 11))
            // (Num 100)) (set (q (Num 12)) (Num 200)) and (g 41) of (uni 11 12) and (uni 15 16): q keeps (Num 100).
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
            var add = Int64Primitives.Add;
            s.Assume(t.H.Of(classes[1]).Is(1L), t.H.Of(classes[2]).Is(2L));
            s.Assume(q.Of(classes[11]).Is(t.Num.Of(100L)), q.Of(classes[12]).Is(t.Num.Of(200L)));
            var conflicting = uni.Of(1L, 2L);
            var other = uni.Of(5L, 6L);
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(40L).Is(conflictFirst ? add.Of(conflicting, other) : add.Of(other, conflicting))));
            Assert.Equal(6L, s.Get(t.G.Of(40L)));
            Assert.True(s.AreEqual(classes[5], classes[6]));
            Assert.True(s.AreEqual(classes[1], classes[2]));
            Assert.Equal(1L, s.Get(t.H.Of(classes[2])));

            conflicting = uni.Of(11L, 12L);
            other = uni.Of(15L, 16L);
            Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(41L).Is(conflictFirst ? add.Of(conflicting, other) : add.Of(other, conflicting))));
            Assert.Equal(26L, s.Get(t.G.Of(41L)));
            Assert.True(s.AreEqual(classes[15], classes[16]));
            Assert.True(s.AreEqual(classes[11], classes[12]));
            Assert.True(s.Check(q.Of(classes[12]).Is(t.Num.Of(100L))));
            Assert.False(s.Check(t.Num.Of(100L).Is(t.Num.Of(200L))));
            Assert.False(s.IsFaulted);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AHeldUnionThatConflictsAmongManyStandsWithThem(bool inChild)
        {
            // One call that unions two classes whose rows of q conflict, then 500 pairs of classes with no rows, over a q
            // of 2000 rows: the fact fails after its rebuild, as egglog's command with (uni ...) calls of each, and every
            // union stands, the conflicting one too, whose rows' rebuild keeps q's row of the first class.
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
            Assert.True(s.AreEqual(keyed[0], keyed[1]));
            Assert.True(s.Check(q.Of(keyed[1]).Is(t.Num.Of(10000L))));
            Assert.Equal(keyed.Length - 1, s.CountRows(q));
            for (var i = 0; i < Pairs; i++)
                Assert.True(s.AreEqual(free[2 * i], free[2 * i + 1]));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheRebuildOfAFactsUnionsKeepsTheRowStoredFirstWhenBothMove(bool inChild)
        {
            // (Num 0) (Num 1) (Num 2) (set (h (Num 2)) 2) (set (h (Num 1)) 1) (fail (set (g 50) (+ (uni 0 1) (uni 0 2)))),
            // "Illegal merge attempted for function h": both rows move to (Num 0), whose id is the smallest, and the
            // rebuild keeps the one stored first, (h (Num 0)) -> 2; (g 50) is 0.
            var t = new Theory();
            var s = t.Open(inChild);
            var classes = new System.Collections.Generic.Dictionary<long, Node>();
            foreach (var key in new[] { 0L, 1L, 2L })
                classes[key] = s.Make(t.Num.Of(key));

            var uni = Primitive.CreateWrite("uni", [typeof(long), typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Union(classes[(long)args[0]], classes[(long)args[1]]);
                result = (long)args[0];
                return true;
            });
            s.Assume(t.H.Of(classes[2]).Is(2L), t.H.Of(classes[1]).Is(1L));
            var e = Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(50L).Is(Int64Primitives.Add.Of(uni.Of(0L, 1L), uni.Of(0L, 2L)))));
            Assert.Equal(2L, e.StoredValue);
            Assert.Equal(1L, e.NewValue);
            Assert.Equal(0L, s.Get(t.G.Of(50L)));
            Assert.True(s.AreEqual(classes[0], classes[2]));
            Assert.True(s.AreEqual(classes[0], classes[1]));
            Assert.Equal(2L, s.Get(t.H.Of(classes[0])));
            Assert.Equal(1, s.CountRows(t.H));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TwoFunctionsThatConflictInOneRebuildEachKeepARow(bool inChild)
        {
            // (Num 1) (Num 2) (Num 3) (Num 4) (set (h2 (Num 1)) 5) (set (h2 (Num 2)) 6) (set (h (Num 1)) 1)
            // (set (h (Num 2)) 2) (fail (set (g 60) (+ (uni 3 4) (uni 2 1)))), with h defined before h2: "Illegal merge
            // attempted for function h", the first function the rebuild merges; both unions stand, (g 60) is 5, h keeps 1
            // and h2 keeps 5.
            var t = new Theory();
            var h2 = t.Graph.DefineFunction<Node, long>("h2");
            var s = t.Open(inChild);
            var classes = new System.Collections.Generic.Dictionary<long, Node>();
            foreach (var key in new[] { 1L, 2L, 3L, 4L })
                classes[key] = s.Make(t.Num.Of(key));

            var uni = Primitive.CreateWrite("uni", [typeof(long), typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Union(classes[(long)args[0]], classes[(long)args[1]]);
                result = (long)args[0];
                return true;
            });
            s.Assume(h2.Of(classes[1]).Is(5L), h2.Of(classes[2]).Is(6L), t.H.Of(classes[1]).Is(1L), t.H.Of(classes[2]).Is(2L));
            var e = Assert.Throws<FactConflictException>(() => s.Assume(t.G.Of(60L).Is(Int64Primitives.Add.Of(uni.Of(3L, 4L), uni.Of(2L, 1L)))));
            Assert.Equal("Illegal merge attempted for function h", e.InnerException!.Message);
            Assert.Same(t.H, e.Table);
            Assert.Equal(1L, e.StoredValue);
            Assert.Equal(2L, e.NewValue);
            Assert.Equal(5L, s.Get(t.G.Of(60L)));
            Assert.True(s.AreEqual(classes[3], classes[4]));
            Assert.True(s.AreEqual(classes[1], classes[2]));
            Assert.Equal(1L, s.Get(t.H.Of(classes[2])));
            Assert.Equal(5L, s.Get(h2.Of(classes[2])));
            Assert.Equal(1, s.CountRows(t.H));
            Assert.Equal(1, s.CountRows(h2));
            Assert.False(s.IsFaulted);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheConflictNamesTheFunctionTheRebuildFailsOn(bool inChild)
        {
            // (constructor W (Math) Math) (function q (Math) Math :no-merge) (set (h (W (Num 1))) 1)
            // (set (h (W (Num 2))) 2) (set (q (Num 1)) (Num 3)) (set (q (Num 2)) (Num 4)) (union (Num 1) (Num 2)):
            // "Illegal merge attempted for function q", whose rows meet in the rebuild's first pass, h's only in the
            // second; h keeps 1 and q (Num 3), and the union stands (probe t1.egg).
            var t = new Theory();
            var w = t.Graph.DefineConstructor<Node, Node>("W");
            var q = t.Graph.DefineFunction<Node, Node>("q");
            var s = t.Open(inChild);
            s.Assume(t.H.Of(w.Of(t.Num.Of(1L))).Is(1L));
            s.Assume(t.H.Of(w.Of(t.Num.Of(2L))).Is(2L));
            s.Assume(q.Of(t.Num.Of(1L)).Is(t.Num.Of(3L)));
            s.Assume(q.Of(t.Num.Of(2L)).Is(t.Num.Of(4L)));
            var e = Assert.Throws<FactConflictException>(() => s.Union(s.Make(t.Num.Of(1L)), s.Make(t.Num.Of(2L))));
            Assert.Equal("Illegal merge attempted for function q", e.InnerException!.Message);
            Assert.Same(q, e.Table);
            Assert.True(s.Check(t.Num.Of(1L).Is(t.Num.Of(2L))));
            Assert.Equal(1L, s.Get(t.H.Of(w.Of(t.Num.Of(2L)))));
            Assert.True(s.Check(q.Of(t.Num.Of(2L)).Is(t.Num.Of(3L))));
            Assert.Equal(1, s.CountRows(t.H));
            Assert.Equal(1, s.CountRows(q));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheRowAtTheCanonicalKeyIsKeptOverOneThatMoves(bool inChild)
        {
            // (constructor W (Math) Math) (function q (Math) Math :no-merge) (set (q (W (Num 3))) (Num 5)), then in a push
            // or not (set (q (Num 5)) (Num 2)) (union (W (Num 3)) (Num 5)): "Illegal merge attempted for function q", and
            // q keeps (Num 5), the row written first, before the push (probe t2.egg); (Num 2) and (Num 5) stay apart.
            // In the child the row written first is the ancestor's, and the child's own moves onto it.
            var t = new Theory();
            var w = t.Graph.DefineConstructor<Node, Node>("W");
            var q = t.Graph.DefineFunction<Node, Node>("q");
            t.Root.Assume(q.Of(w.Of(t.Num.Of(3L))).Is(t.Num.Of(5L)));
            var s = t.Open(inChild);
            s.Assume(q.Of(t.Num.Of(5L)).Is(t.Num.Of(2L)));
            var e = Assert.Throws<FactConflictException>(() => s.Union(s.Make(w.Of(t.Num.Of(3L))), s.Make(t.Num.Of(5L))));
            Assert.Same(q, e.Table);
            Assert.False(s.IsFaulted);
            Assert.True(s.Check(w.Of(t.Num.Of(3L)).Is(t.Num.Of(5L))));
            Assert.True(s.Check(q.Of(t.Num.Of(5L)).Is(t.Num.Of(5L))));
            Assert.False(s.Check(t.Num.Of(2L).Is(t.Num.Of(5L))));
            Assert.Equal(1, s.CountRows(q));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheRowOfTheContainerMadeFirstIsKept(bool inChild)
        {
            // (constructor W (Math) Math) (constructor S (MS) Math) with MS (Set Math), (function g (MS) i64 :no-merge),
            // (function mx (Math) i64 :merge (max old new)): (union (Num 2) (W (Num 1)))
            // (set (mx (S (set-of (Num 5) (Num 0)))) 3) (set (g (set-of (Num 5) (Num 2))) 3)
            // (set (g (set-of (Num 5) (Num 0))) 1), then in a push or not (union (Num 0) (Num 2)): "Illegal merge
            // attempted for function g", and g keeps 1, the row of (set-of (Num 5) (Num 0)), the container made first,
            // though its row was written second. In the child both rows are the ancestor's, the one of the container
            // the child's container pass rebuilds in place re-stamped, the other moving onto it.
            var t = new Theory();
            t.Graph.Values.RegisterSet<Node>();
            var w = t.Graph.DefineConstructor<Node, Node>("W");
            var sc = t.Graph.DefineConstructor<Set<Node>, Node>("S");
            var g = t.Graph.DefineFunction<Set<Node>, long>("gs");
            var mx = t.Graph.DefineFunction<Node, long>("mx", ValueLattice.Max<long>());
            var of = Primitives.SetOf<Node>();
            t.Root.Union(t.Root.Make(t.Num.Of(2L)), t.Root.Make(w.Of(t.Num.Of(1L))));
            t.Root.Assume(mx.Of(sc.Of(of.Of(t.Num.Of(5L), t.Num.Of(0L)))).Is(3L));
            t.Root.Assume(g.Of(of.Of(t.Num.Of(5L), t.Num.Of(2L))).Is(3L));
            t.Root.Assume(g.Of(of.Of(t.Num.Of(5L), t.Num.Of(0L))).Is(1L));
            var s = t.Open(inChild);
            var e = Assert.Throws<FactConflictException>(() => s.Union(s.Make(t.Num.Of(0L)), s.Make(t.Num.Of(2L))));
            Assert.Same(g, e.Table);
            Assert.Equal(1L, e.StoredValue);
            Assert.Equal(3L, e.NewValue);
            Assert.False(s.IsFaulted);
            Assert.Equal(1L, s.Get(g.Of(of.Of(t.Num.Of(5L), t.Num.Of(0L)))));
            Assert.Equal(1, s.CountRows(g));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFactWhoseUnionConflictsAfterAnEarlierUnionCallsItsPrimitiveOnce(bool inChild)
        {
            // (Lit 1) (Num 2) (Num 3) (Num 4) (set (h (Lit 1)) 1) (set (h (Num 2)) 2) (union (Num 3) (Num 4))
            // (union (Lit (+ 0 1)) (Num 2)): "Illegal merge attempted for function h", the union stands, and h keeps 1
            // (probe t4.egg). egglog evaluates the command's call once; so does the batch, which drops the fact, flushes
            // the earlier union and plans the fact again with the result its first plan computed.
            var t = new Theory();
            var s = t.Open(inChild);
            var calls = 0;
            var pure = Primitive.Create<long, long>("pure", x =>
            {
                calls++;
                return x;
            });
            var one = s.Make(t.Lit.Of(1L));
            var two = s.Make(t.Num.Of(2L));
            var three = s.Make(t.Num.Of(3L));
            var four = s.Make(t.Num.Of(4L));
            s.Assume(t.H.Of(one).Is(1L), t.H.Of(two).Is(2L));
            s.Write(w =>
            {
                w.Union(three, four);
                Assert.Throws<FactConflictException>(() => w.Assume(t.Lit.Of(pure.Of(1L)).Is(t.Num.Of(2L))));
            });
            Assert.Equal(1, calls);
            Assert.True(s.AreEqual(three, four));
            Assert.True(s.AreEqual(one, two));
            Assert.Equal(1L, s.Get(t.H.Of(two)));
            Assert.Equal(1, s.CountRows(t.H));
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ABatchsRowsAndTheUnionThatMakesThemMeetKeepEgglogsRowInAChild(bool frozen)
        {
            // (W (Num 4)) (push) (set (h (W (Num 0))) 2) (set (h (W (Num 4))) 1) (union (W (Num 4)) (W (Num 0))):
            // "Illegal merge attempted for function h", and h keeps 1, the row at the key the rebuild keeps (probe
            // batch-row.egg). The batch flushes the rows with the union; a child merges the rows before the union, as
            // egglog's flush leaves them unrebuilt, so the rebuild moves (W (Num 0))'s row onto the other's.
            var graph = new EGraph();
            graph.DefineSort<Node>("M");
            var num = graph.DefineConstructor<long, Node>("Num");
            var w = graph.DefineConstructor<Node, Node>("W");
            var h = graph.DefineFunction<Node, long>("h");
            if (frozen)
                graph.Root.Freeze();

            var s = graph.Root.CreateChild();
            s.Make(w.Of(num.Of(4L)));
            s.Write(writer =>
            {
                writer.Assume(h.Of(w.Of(num.Of(0L))).Is(2L), h.Of(w.Of(num.Of(4L))).Is(1L));
                var e = Assert.Throws<FactConflictException>(() => writer.Assume(((Term<Node>)w.Of(num.Of(4L))).Is(w.Of(num.Of(0L)))));
                Assert.Equal(1L, e.StoredValue);
            });
            Assert.Equal(1L, s.Get(h.Of(w.Of(num.Of(0L)))));
            Assert.Equal(1, s.CountRows(h));
            Assert.False(s.IsFaulted);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFactsWriteAndTheUnionItsPrimitiveHeldKeepEgglogsRowInAChild(bool frozen)
        {
            // (push) (union (Num 0) (Num 1)) (set (q (Num 5)) (Num 4))
            // (set (q (Num 0)) (vec-get (vec-union (vec-of (Num 5)) (vec-of (Num 1))) 0)): "Illegal merge attempted for
            // function q", and q keeps (Num 4) at (Num 0), the row the table holds first of the two the rebuild moves
            // (probe child-held-row.egg), in a child as at the root.
            var graph = new EGraph();
            graph.DefineSort<Node>("M");
            graph.Values.RegisterVec<Node>();
            var num = graph.DefineConstructor<long, Node>("Num");
            var q = graph.DefineFunction<Node, Node>("q");
            if (frozen)
                graph.Root.Freeze();

            var s = graph.Root.CreateChild();
            s.Union(s.Make(num.Of(0L)), s.Make(num.Of(1L)));
            s.Assume(q.Of(num.Of(5L)).Is(num.Of(4L)));
            var of = Primitives.VecOf<Node>();
            var held = Primitives.VecGet<Node>().Of(Primitives.VecUnion<Node>().Of(of.Of(num.Of(5L)), of.Of(num.Of(1L))), 0L);
            Assert.Throws<FactConflictException>(() => s.Assume(q.Of(num.Of(0L)).Is(held)));
            Assert.True(s.Check(q.Of(num.Of(0L)).Is(num.Of(4L))));
            Assert.Equal(1, s.CountRows(q));
            Assert.False(s.IsFaulted);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFactsWriteThatTheUnionItsPrimitiveHeldMakesEqualStandsInAChild(bool frozen)
        {
            // (push) (set (q (Num 4)) (Num 0)) (union (Num 0) (Num 5))
            // (set (q (Num 5)) (vec-get (vec-union (vec-of (Num 4)) (vec-of (Num 0))) 0)): the union makes (Num 4) one
            // with (Num 0) and (Num 5), so the two rows meet holding one class, and egglog's command succeeds (probe
            // child-false-fail.egg).
            var graph = new EGraph();
            graph.DefineSort<Node>("M");
            graph.Values.RegisterVec<Node>();
            var num = graph.DefineConstructor<long, Node>("Num");
            var q = graph.DefineFunction<Node, Node>("q");
            if (frozen)
                graph.Root.Freeze();

            var s = graph.Root.CreateChild();
            s.Assume(q.Of(num.Of(4L)).Is(num.Of(0L)));
            s.Union(s.Make(num.Of(0L)), s.Make(num.Of(5L)));
            var of = Primitives.VecOf<Node>();
            s.Assume(q.Of(num.Of(5L)).Is(Primitives.VecGet<Node>().Of(Primitives.VecUnion<Node>().Of(of.Of(num.Of(4L)), of.Of(num.Of(0L))), 0L)));
            Assert.True(s.Check(q.Of(num.Of(5L)).Is(num.Of(0L))));
            Assert.Equal(1, s.CountRows(q));
            Assert.False(s.IsFaulted);
        }

    }

}
