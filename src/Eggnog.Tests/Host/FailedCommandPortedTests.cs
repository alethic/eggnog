using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Host
{

    /// <summary>
    /// What a failed command keeps, as egglog's: its actions run as a rule (src/lib.rs:1171-1195), whose writes staged
    /// before the panic outlive it, so the terms its calls made and what its primitives wrote stand, while its own write
    /// does not happen, or is refused where it conflicts. Each program ran on egglog's binary at 5e655cae as the comments
    /// say.
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

        [Fact]
        public void ADeletionAPrimitiveHeldGoesBackWhenItsWriteConflicts()
        {
            // (set (k 0) 1); a write primitive p deletes (C a), sets (k 0) to 2 and returns a: (C 1) made, then a
            // subsumption of (C (p 1)) whose held write conflicts. The conflict is the failure, and (C 1) stands.
            var t = new Theory();
            var c = t.Graph.DefineConstructor<long, Node>("C");
            var k = t.Graph.DefineFunction<long, long>("k");
            t.Root.Assume(k.Of(0L).Is(1L));
            var p = Primitive.CreateWrite("p", [typeof(long)], (ITableWriter tables, ReadOnlySpan<object> args, out long result) =>
            {
                tables.Remove(c, args[0]);
                tables.Set(k, [0L], 2L);
                result = (long)args[0];
                return true;
            });
            Assert.Throws<FactConflictException>(() => t.Root.Write(w =>
            {
                w.Make(c.Of(1L));
                w.Subsume(c.Of(p.Of(1L)));
            }));
            Assert.True(t.Has(c.Of(1L)));
            Assert.Equal(1L, t.Root.Get(k.Of(0L)));
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
