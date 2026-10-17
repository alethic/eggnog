using System;
using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// egglog's push/pop programs, translated by hand: a push is <see cref="EGraphContext.CreateChild"/> and a pop
    /// <see cref="EGraphContext.Dispose"/> (see <see cref="EggPushPopProgram"/> for the command mapping). Every rule of
    /// these programs may inherit watermarks, so egglog's push, which copies <c>last_run_at</c>, coincides with
    /// inheritance; each test asserts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expected values come from egglog itself: the <c>egglog</c> CLI (egglog 2.0.0) built from a copy of
    /// <c>ext/egglog</c> at commit <c>5e655cae</c> with <c>cargo +1.91.0 build --release --bin egglog</c>. Each test
    /// names the file and the command. The instrumented copies add <c>(print-size)</c> around pushes and pops, and
    /// iteration counts are the <c>Finished ruleset</c> lines that <c>RUST_LOG=debug</c> prints once per iteration.
    /// Ids are not compared (a child mints from its own blocks, egglog from one counter), only sizes, iteration counts
    /// and checks.
    /// </para>
    /// <para>
    /// The other push/pop programs of <c>ext/egglog/tests</c> are not translated. <c>container-rebuild.egg</c>,
    /// <c>container-fail.egg</c> and the eq-container half of <c>vec.egg</c> intern or rebuild <c>Vec</c> containers of
    /// e-classes inside a push: they wait on eq-sort containers in children, which children refuse (a child cannot
    /// intern one, and a child union that displaces an element of one is refused), and would be translated with
    /// <see cref="VecContainer"/> once children have them. The rest are out of scope: the <c>UnstableFn</c> half of
    /// <c>vec.egg</c>, <c>python_array_optimize.egg</c> and <c>stresstest_large_expr.egg</c> need function values (a
    /// container that names a function, called from inside a rule), which the library does not offer, and the last two
    /// also rulesets of egglog's frontend; the last two blocks of <c>tricky-type-checking.egg</c> (issue #113) only
    /// exercise egglog's type checker choosing between two <c>Map</c> sorts, which has no counterpart here, and have no
    /// <c>check</c>, so there is nothing to compare.
    /// </para>
    /// </remarks>
    public class EggPushPopTests
    {

        /// <summary>
        /// <c>ext/egglog/tests/calc.egg</c>: group axioms with a generator of order four, four push/pop blocks each
        /// running until a goal equality holds.
        /// </summary>
        /// <remarks>
        /// Expected values: <c>calc-observed.egg</c> (<c>calc.egg</c> with <c>(print-size)</c> before every
        /// <c>(push)</c> and before and after every <c>(pop)</c>), run as
        /// <c>RUST_LOG=debug egglog calc-observed.egg</c>. Blocks: 0, 1, 2 and 1 iterations; <c>g*</c> sizes 3, 11, 39
        /// and 12 and <c>inv</c> sizes 0, 0, 2 and 1 in the push; <c>g* 3</c>, <c>inv 0</c> and one row in each nullary
        /// constructor at the root before and after each.
        /// </remarks>
        [Fact]
        public void Calc()
        {
            var p = new EggPushPopProgram();
            var g = p.EGraph;

            // (datatype G) (constructor IConst () G) (let $I (IConst)) ... (constructor inv (G) G)
            var iconst = p.Constructor("IConst");
            var i = p.Term(iconst);
            var aconst = p.Constructor("AConst");
            var a = p.Term(aconst);
            var bconst = p.Constructor("BConst");
            p.Term(bconst);
            var mul = p.Constructor("g*", ColumnTy.Id, ColumnTy.Id);
            var inv = p.Constructor("inv", ColumnTy.Id);

            var rules = new List<EGraphRuleId>();

            // (birewrite (g* (g* a b) c) (g* a (g* b c))) ; assoc
            {
                var rb = g.NewRule("assoc", true);
                var (x, y, z, xy, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(mul, [x, y, xy], false);
                rb.QueryTable(mul, [xy, z, e], false);
                var yz = rb.Lookup(mul, [y, z], static () => "g*");
                rb.Set(mul, [x, yz, e]);
                rules.Add(rb.Build());

                rb = g.NewRule("assoc-rev", true);
                (x, y, z) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                var (yz2, e2) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(mul, [y, z, yz2], false);
                rb.QueryTable(mul, [x, yz2, e2], false);
                var xy2 = rb.Lookup(mul, [x, y], static () => "g*");
                rb.Set(mul, [xy2, z, e2]);
                rules.Add(rb.Build());
            }

            // (rewrite (g* $I a) a) ; idl     (rewrite (g* a $I) a) ; idr
            for (var side = 0; side < 2; side++)
            {
                var rb = g.NewRule(side == 0 ? "idl" : "idr", true);
                var (one, x, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(iconst, [one], false);
                rb.QueryTable(mul, side == 0 ? [one, x, e] : [x, one, e], false);
                rb.Union(e, x);
                rules.Add(rb.Build());
            }

            // (rewrite (g* (inv a) a) $I) ; invl     (rewrite (g* a (inv a)) $I) ; invr
            for (var side = 0; side < 2; side++)
            {
                var rb = g.NewRule(side == 0 ? "invl" : "invr", true);
                var (x, ix, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(inv, [x, ix], false);
                rb.QueryTable(mul, side == 0 ? [ix, x, e] : [x, ix, e], false);
                var one = rb.Lookup(iconst, [], static () => "$I");
                rb.Union(e, one);
                rules.Add(rb.Build());
            }

            // (rewrite (g* $A (g* $A (g* $A $A))) $I) ; $A is cyclic of period 4
            {
                var rb = g.NewRule("cyclic", true);
                var (ga, t1, t2, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(aconst, [ga], false);
                rb.QueryTable(mul, [ga, ga, t1], false);
                rb.QueryTable(mul, [ga, t1, t2], false);
                rb.QueryTable(mul, [ga, t2, e], false);
                var one = rb.Lookup(iconst, [], static () => "$I");
                rb.Union(e, one);
                rules.Add(rb.Build());
            }

            var all = rules.ToArray();

            // (let $A2 (g* $A $A)) (let $A4 (g* $A2 $A2)) (let $A8 (g* $A4 $A4))
            var a2 = p.Term(mul, a, a);
            var a4 = p.Term(mul, a2, a2);
            p.Term(mul, a4, a4);
            AssertRootSizes(p, mul, inv, iconst, aconst, bconst);

            // (push) (g* $A4 $A4) (run 10000 :until (= (g* $A4 $A4) (g* (g* $A2 $A2) (g* $A2 $A2)))) (check ...) (pop)
            p.Push();
            p.Term(mul, a4, a4);
            bool Goal1() => p.Equal(p.TryTerm(mul, a4, a4), p.TryTerm(mul, p.TryTerm(mul, a2, a2), p.TryTerm(mul, a2, a2)));
            Assert.Equal(0, p.Run(all, 10000, Goal1));
            Assert.True(Goal1());
            Assert.Equal(3, p.Size(mul));
            Assert.Equal(0, p.Size(inv));
            p.Pop();
            AssertRootSizes(p, mul, inv, iconst, aconst, bconst);

            // (push) (g* (g* $A2 $A2) (g* $A2 $A2))
            // (run 10000 :until (= (g* (g* $A2 $A2) (g* $A2 $A2)) (g* $A2 (g* $A2 (g* $A2 $A2))))) (check ...) (pop)
            p.Push();
            p.Term(mul, p.Term(mul, a2, a2), p.Term(mul, a2, a2));
            bool Goal2() => p.Equal(p.TryTerm(mul, p.TryTerm(mul, a2, a2), p.TryTerm(mul, a2, a2)), p.TryTerm(mul, a2, p.TryTerm(mul, a2, p.TryTerm(mul, a2, a2))));
            Assert.False(Goal2());
            Assert.Equal(1, p.Run(all, 10000, Goal2));
            Assert.True(Goal2());
            Assert.Equal(11, p.Size(mul));
            Assert.Equal(0, p.Size(inv));
            p.Pop();
            AssertRootSizes(p, mul, inv, iconst, aconst, bconst);

            // (constructor aConst () G) (constructor bConst () G) (let $a (aConst)) (let $b (bConst))
            var lowerA = p.Constructor("aConst");
            var lowerB = p.Constructor("bConst");
            var la = p.Term(lowerA);
            var lb = p.Term(lowerB);
            AssertRootSizes(p, mul, inv, iconst, aconst, bconst, lowerA, lowerB);

            // (push) (g* (g* $b (g* (inv $a) $a)) (inv $b))
            // (run 100000 :until (= (g* (g* $b (g* (inv $a) $a)) (inv $b)) (g* $b (inv $b)))) (check ...) (pop)
            p.Push();
            p.Term(mul, p.Term(mul, lb, p.Term(mul, p.Term(inv, la), la)), p.Term(inv, lb));
            Value? Lhs3() => p.TryTerm(mul, p.TryTerm(mul, lb, p.TryTerm(mul, p.TryTerm(inv, la), la)), p.TryTerm(inv, lb));
            bool Goal3() => p.Equal(Lhs3(), p.TryTerm(mul, lb, p.TryTerm(inv, lb)));
            Assert.Equal(2, p.Run(all, 100000, Goal3));
            Assert.True(Goal3());
            Assert.Equal(39, p.Size(mul));
            Assert.Equal(2, p.Size(inv));
            p.Pop();
            AssertRootSizes(p, mul, inv, iconst, aconst, bconst, lowerA, lowerB);

            // (push) (g* $b (inv $b)) (run 100000 :until (= (g* $b (inv $b)) $I)) (check ...) (pop)
            p.Push();
            p.Term(mul, lb, p.Term(inv, lb));
            bool Goal4() => p.Equal(p.TryTerm(mul, lb, p.TryTerm(inv, lb)), i);
            Assert.Equal(1, p.Run(all, 100000, Goal4));
            Assert.True(Goal4());
            Assert.Equal(12, p.Size(mul));
            Assert.Equal(1, p.Size(inv));
            p.Pop();
            AssertRootSizes(p, mul, inv, iconst, aconst, bconst, lowerA, lowerB);

            p.AssertAllInherit(all);
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// <c>ext/egglog/tests/tricky-type-checking.egg</c>, blocks one to six (each a push/pop): rules over literals
        /// and primitives without atoms, and issue #80's category typing with conditional birewrites.
        /// </summary>
        /// <remarks>
        /// Expected values: <c>tricky-observed.egg</c> (the file with <c>(print-size)</c> before every <c>(pop)</c>),
        /// run as <c>RUST_LOG=debug egglog tricky-observed.egg</c>, and <c>egglog tricky-type-checking.egg</c> for the
        /// <c>print-function</c> output. Blocks one to five: one iteration each, which changes nothing in blocks one to
        /// three and adds one row in four and five; <c>f</c> ends empty, <c>R</c> with one row. Block six: five
        /// iterations (the fifth changes nothing); sizes <c>AConst</c>, <c>BConst</c>, <c>Ob</c>, <c>fConst</c>,
        /// <c>gConst</c> 1, <c>Hom</c> 4, <c>compose</c> 9, <c>id</c> 2, <c>type</c> 7; <c>type</c> maps
        /// <c>(AConst)</c> and <c>(BConst)</c> to <c>(Ob)</c>, <c>(compose (gConst) (fConst))</c> to
        /// <c>(Hom (BConst) (BConst))</c> and <c>(id X)</c> to <c>(Hom X X)</c>.
        /// </remarks>
        [Fact]
        public void TrickyTypeChecking()
        {
            var p = new EggPushPopProgram();
            var g = p.EGraph;
            var identity = p.Primitive(static (_, args) => args[0]);
            var plus = p.Primitive((state, args) => p.Int(state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0])) + state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1]))).Rep);
            var one = RuleEntry.Const(p.Int(1), p.I64);
            var two = RuleEntry.Const(p.Int(2), p.I64);
            var unit = RuleEntry.Const(p.Unit, p.UnitType);
            var allRules = new List<EGraphRuleId>();

            // repro-constraineq: (push) (rule ((= x 1) (= y x) (= z y)) ()) (run 1) (pop)
            p.Push();
            {
                var rb = g.NewRule("constraineq", true);
                var (x, y, z) = (rb.NewVar(p.I64), rb.NewVar(p.I64), rb.NewVar(p.I64));
                rb.QueryPrim(identity, [one, x], p.I64);
                rb.QueryPrim(identity, [x, y], p.I64);
                rb.QueryPrim(identity, [y, z], p.I64);
                var rule = rb.Build();
                allRules.Add(rule);
                Assert.Equal(1, p.Run([rule], 1));
            }

            p.Pop();

            // repro-constraineq2: (push) (rule ((= x 1) (= y x)) ()) (run 1) (pop)
            p.Push();
            {
                var rb = g.NewRule("constraineq2", true);
                var (x, y) = (rb.NewVar(p.I64), rb.NewVar(p.I64));
                rb.QueryPrim(identity, [one, x], p.I64);
                rb.QueryPrim(identity, [x, y], p.I64);
                var rule = rb.Build();
                allRules.Add(rule);
                Assert.Equal(1, p.Run([rule], 1));
            }

            p.Pop();

            // repro-constraineq3: (push) (relation f (i64)) (rule ((= x 1) (= x 2)) ((f x))) (run 1) (print-function f 10) (pop)
            p.Push();
            {
                var f = p.Relation("f", p.I64);
                var rb = g.NewRule("constraineq3", true);
                var x = rb.NewVar(p.I64);
                rb.QueryPrim(identity, [one, x], p.I64);
                rb.QueryPrim(identity, [two, x], p.I64);
                rb.Set(f, [x, unit]);
                var rule = rb.Build();
                allRules.Add(rule);
                Assert.Equal(1, p.Run([rule], 1));
                Assert.Equal(0, p.Size(f));
            }

            p.Pop();

            // Issue #196: (push) (relation R (i64)) (rule ((= x y) (= y 1)) ((R x))) (run 1) (check (R 1)) (pop)
            p.Push();
            {
                var r = p.Relation("R", p.I64);
                var rb = g.NewRule("issue-196", true);
                var (x, y) = (rb.NewVar(p.I64), rb.NewVar(p.I64));
                rb.QueryPrim(identity, [one, y], p.I64);
                rb.QueryPrim(identity, [y, x], p.I64);
                rb.Set(r, [x, unit]);
                var rule = rb.Build();
                allRules.Add(rule);
                Assert.Equal(1, p.Run([rule], 1));
                Assert.Equal(1, p.Size(r));
                Assert.True(p.Current.ContainsKey(r, [p.Int(1)]));
            }

            p.Pop();

            // (push) (relation R (i64)) (rule ((= x (+ y 1)) (= y 1)) ((R x))) (run 1) (check (R 2)) (pop)
            // The second R is a second function here: egglog's pop dropped the first, which this e-graph keeps.
            p.Push();
            {
                var r = p.Relation("R'", p.I64);
                var rb = g.NewRule("issue-196-plus", true);
                var (x, y) = (rb.NewVar(p.I64), rb.NewVar(p.I64));
                rb.QueryPrim(identity, [one, y], p.I64);
                rb.QueryPrim(plus, [y, one, x], p.I64);
                rb.Set(r, [x, unit]);
                var rule = rb.Build();
                allRules.Add(rule);
                Assert.Equal(1, p.Run([rule], 1));
                Assert.Equal(1, p.Size(r));
                Assert.True(p.Current.ContainsKey(r, [p.Int(2)]));
                Assert.False(p.Current.ContainsKey(r, [p.Int(1)]));
            }

            p.Pop();
            Assert.Equal(0, p.Depth);

            // Issue #80, in a push.
            p.Push();
            var issue80 = Issue80(p);
            allRules.AddRange(issue80);
            p.Pop();

            p.AssertAllInherit(allRules);
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// The issue #80 block of <c>tricky-type-checking.egg</c>, run in the current (pushed) context; returns its
        /// rules.
        /// </summary>
        static EGraphRuleId[] Issue80(EggPushPopProgram p)
        {
            var g = p.EGraph;

            // (datatype TYPE) (datatype TERM) (constructor type (TERM) TYPE) (constructor Ob () TYPE)
            // (constructor Hom (TERM TERM) TYPE) (constructor id (TERM) TERM) (constructor compose (TERM TERM) TERM)
            var type = p.Constructor("type", ColumnTy.Id);
            var ob = p.Constructor("Ob");
            var hom = p.Constructor("Hom", ColumnTy.Id, ColumnTy.Id);
            var id = p.Constructor("id", ColumnTy.Id);
            var compose = p.Constructor("compose", ColumnTy.Id, ColumnTy.Id);
            var rules = new List<EGraphRuleId>();

            // (rule ((type (id A))) ((type A)))
            {
                var rb = g.NewRule("type-id", true);
                var (a, ia) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(id, [a, ia], false);
                rb.QueryTable(type, [ia, rb.NewVar(ColumnTy.Id)], false);
                rb.Lookup(type, [a], static () => "type");
                rules.Add(rb.Build());
            }

            // (rewrite (type (id A)) (Hom A A) :when ((= (type A) (Ob))))
            {
                var rb = g.NewRule("type-id-hom", true);
                var (a, ia, e, o) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(id, [a, ia], false);
                rb.QueryTable(type, [ia, e], false);
                rb.QueryTable(type, [a, o], false);
                rb.QueryTable(ob, [o], false);
                rb.Set(hom, [a, a, e]);
                rules.Add(rb.Build());
            }

            // (rule ((type (compose f g))) ((type f) (type g)))
            {
                var rb = g.NewRule("type-compose", true);
                var (f, h, c) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(compose, [f, h, c], false);
                rb.QueryTable(type, [c, rb.NewVar(ColumnTy.Id)], false);
                rb.Lookup(type, [f], static () => "type");
                rb.Lookup(type, [h], static () => "type");
                rules.Add(rb.Build());
            }

            // (rewrite (type (compose f g)) (Hom A C) :when ((= (type f) (Hom A B)) (= (type g) (Hom B C))))
            {
                var rb = g.NewRule("type-compose-hom", true);
                var (f, h, c, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                var (a, b, cc, tf, th) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(compose, [f, h, c], false);
                rb.QueryTable(type, [c, e], false);
                rb.QueryTable(type, [f, tf], false);
                rb.QueryTable(hom, [a, b, tf], false);
                rb.QueryTable(type, [h, th], false);
                rb.QueryTable(hom, [b, cc, th], false);
                rb.Set(hom, [a, cc, e]);
                rules.Add(rb.Build());
            }

            // (birewrite (compose (compose f g) h) (compose f (compose g h))
            //     :when ((= (type A) (Ob)) (= (type B) (Ob)) (= (type C) (Ob)) (= (type D) (Ob))
            //            (= (type f) (Hom A B)) (= (type g) (Hom B C)) (= (type h) (Hom C D))))
            for (var direction = 0; direction < 2; direction++)
            {
                var rb = g.NewRule(direction == 0 ? "assoc" : "assoc-rev", true);
                var (f, gg, h, inner, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                if (direction == 0)
                {
                    rb.QueryTable(compose, [f, gg, inner], false);
                    rb.QueryTable(compose, [inner, h, e], false);
                }
                else
                {
                    rb.QueryTable(compose, [gg, h, inner], false);
                    rb.QueryTable(compose, [f, inner, e], false);
                }

                var objects = new RuleVariable[4];
                for (var k = 0; k < 4; k++)
                {
                    objects[k] = rb.NewVar(ColumnTy.Id);
                    var o = rb.NewVar(ColumnTy.Id);
                    rb.QueryTable(type, [objects[k], o], false);
                    rb.QueryTable(ob, [o], false);
                }

                RuleVariable[] arrows = [f, gg, h];
                for (var k = 0; k < 3; k++)
                {
                    var t = rb.NewVar(ColumnTy.Id);
                    rb.QueryTable(type, [arrows[k], t], false);
                    rb.QueryTable(hom, [objects[k], objects[k + 1], t], false);
                }

                if (direction == 0)
                {
                    var gh = rb.Lookup(compose, [gg, h], static () => "compose");
                    rb.Set(compose, [f, gh, e]);
                }
                else
                {
                    var fg = rb.Lookup(compose, [f, gg], static () => "compose");
                    rb.Set(compose, [fg, h, e]);
                }

                rules.Add(rb.Build());
            }

            // (birewrite (compose f (id B)) f :when ((= (type A) (Ob)) (= (type B) (Ob)) (= (type f) (Hom A B))))
            // (birewrite (compose (id A) f) f :when ((= (type A) (Ob)) (= (type B) (Ob)) (= (type f) (Hom A B))))
            for (var side = 0; side < 2; side++)
            {
                for (var direction = 0; direction < 2; direction++)
                {
                    var rb = g.NewRule((side == 0 ? "id-right" : "id-left") + (direction == 0 ? "" : "-rev"), true);
                    var (f, a, b, t) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                    var identityOn = side == 0 ? b : a;
                    RuleVariable? e = null;
                    if (direction == 0)
                    {
                        var i = rb.NewVar(ColumnTy.Id);
                        e = rb.NewVar(ColumnTy.Id);
                        rb.QueryTable(id, [identityOn, i], false);
                        rb.QueryTable(compose, side == 0 ? [f, i, e.Value] : [i, f, e.Value], false);
                    }

                    foreach (var thing in new[] { a, b })
                    {
                        var o = rb.NewVar(ColumnTy.Id);
                        rb.QueryTable(type, [thing, o], false);
                        rb.QueryTable(ob, [o], false);
                    }

                    rb.QueryTable(type, [f, t], false);
                    rb.QueryTable(hom, [a, b, t], false);
                    if (direction == 0)
                    {
                        rb.Union(e!.Value, f);
                    }
                    else
                    {
                        var i = rb.Lookup(id, [identityOn], static () => "id");
                        rb.Set(compose, side == 0 ? [f, i, f] : [i, f, f]);
                    }

                    rules.Add(rb.Build());
                }
            }

            var all = rules.ToArray();

            // (constructor AConst () TERM) (let $A (AConst)) ... (let $g (gConst)) (let $fog (compose $g $f))
            var aconst = p.Constructor("AConst");
            var bconst = p.Constructor("BConst");
            var fconst = p.Constructor("fConst");
            var gconst = p.Constructor("gConst");
            var oa = p.Term(aconst);
            var ob2 = p.Term(bconst);
            var mf = p.Term(fconst);
            var mg = p.Term(gconst);
            var fog = p.Term(compose, mg, mf);

            // (union (type $f) (Hom $A $B)) (union (type $g) (Hom $B $A)) (union (type $A) (Ob)) (union (type $B) (Ob))
            p.Union(p.Term(type, mf), p.Term(hom, oa, ob2));
            p.Union(p.Term(type, mg), p.Term(hom, ob2, oa));
            p.Union(p.Term(type, oa), p.Term(ob));
            p.Union(p.Term(type, ob2), p.Term(ob));

            // (type $fog) (run 10) (print-function type 10)
            p.Term(type, fog);
            Assert.Equal(5, p.Run(all, 10));
            Assert.Equal(1, p.Size(aconst));
            Assert.Equal(1, p.Size(bconst));
            Assert.Equal(1, p.Size(ob));
            Assert.Equal(1, p.Size(fconst));
            Assert.Equal(1, p.Size(gconst));
            Assert.Equal(4, p.Size(hom));
            Assert.Equal(9, p.Size(compose));
            Assert.Equal(2, p.Size(id));
            Assert.Equal(7, p.Size(type));

            var obj = p.TryTerm(ob);
            Assert.True(p.Equal(p.TryTerm(type, oa), obj));
            Assert.True(p.Equal(p.TryTerm(type, ob2), obj));
            Assert.True(p.Equal(p.TryTerm(type, mf), p.TryTerm(hom, oa, ob2)));
            Assert.True(p.Equal(p.TryTerm(type, mg), p.TryTerm(hom, ob2, oa)));
            Assert.True(p.Equal(p.TryTerm(type, p.TryTerm(id, oa)), p.TryTerm(hom, oa, oa)));
            Assert.True(p.Equal(p.TryTerm(type, p.TryTerm(id, ob2)), p.TryTerm(hom, ob2, ob2)));

            // (check (= (type $f) (type (compose (id $A) (compose $f (id $B))))))
            Assert.True(p.Equal(p.TryTerm(type, mf), p.TryTerm(type, p.TryTerm(compose, p.TryTerm(id, oa), p.TryTerm(compose, mf, p.TryTerm(id, ob2))))));

            // (check (= (type $fog) (Hom $B $B)))
            Assert.True(p.Equal(p.TryTerm(type, fog), p.TryTerm(hom, ob2, ob2)));
            return all;
        }

        /// <summary>
        /// A program written for this test so that a push inherits progress: the root runs one iteration before the
        /// first push, and a second push nests inside the first (a grandchild of a writable child).
        /// </summary>
        /// <remarks>
        /// Expected values: <c>egglog push-inherit.egg</c> with <c>RUST_LOG=debug</c>, the program being:
        /// <code>
        /// (datatype Math (Num i64) (Add Math Math) (Mul Math Math))
        /// (rewrite (Add a b) (Add b a))
        /// (rewrite (Add (Add a b) c) (Add a (Add b c)))
        /// (rewrite (Mul a (Add b c)) (Add (Mul a b) (Mul a c)))
        /// (let $e (Mul (Num 2) (Add (Num 3) (Num 4))))
        /// (run 1)
        /// (print-size)
        /// (push)
        /// (union (Num 3) (Num 4))
        /// (run 2)
        /// (print-size)
        /// (check (= (Add (Num 3) (Num 3)) (Add (Num 4) (Num 4))))
        /// (push)
        /// (let $g (Mul (Num 5) (Add (Num 2) (Num 3))))
        /// (run 10)
        /// (print-size)
        /// (check (= $g (Add (Mul (Num 5) (Num 2)) (Mul (Num 5) (Num 4)))))
        /// (pop)
        /// (print-size)
        /// (run 1)
        /// (print-size)
        /// (pop)
        /// (print-size)
        /// (fail (check (= (Num 3) (Num 4))))
        /// (run 1)
        /// (print-size)
        /// </code>
        /// Runs: 1 iteration (changed), 1 (unchanged), 3 (the third unchanged), 1 (unchanged), 1 (changed).
        /// Sizes (Add, Mul, Num): 3 3 3; 2 2 3; 6 5 4; 2 2 3 after the inner pop and after its run; 3 3 3 after the
        /// outer pop; 4 3 3 at the end.
        /// </remarks>
        [Fact]
        public void PushInheritsProgress()
        {
            var p = new EggPushPopProgram();
            var g = p.EGraph;
            var num = p.Constructor("Num", p.I64);
            var add = p.Constructor("Add", ColumnTy.Id, ColumnTy.Id);
            var mul = p.Constructor("Mul", ColumnTy.Id, ColumnTy.Id);
            var rules = new List<EGraphRuleId>();

            // (rewrite (Add a b) (Add b a))
            {
                var rb = g.NewRule("comm", true);
                var (a, b, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(add, [a, b, e], false);
                rb.Set(add, [b, a, e]);
                rules.Add(rb.Build());
            }

            // (rewrite (Add (Add a b) c) (Add a (Add b c)))
            {
                var rb = g.NewRule("assoc", true);
                var (a, b, c, ab, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(add, [a, b, ab], false);
                rb.QueryTable(add, [ab, c, e], false);
                var bc = rb.Lookup(add, [b, c], static () => "Add");
                rb.Set(add, [a, bc, e]);
                rules.Add(rb.Build());
            }

            // (rewrite (Mul a (Add b c)) (Add (Mul a b) (Mul a c)))
            {
                var rb = g.NewRule("distribute", true);
                var (a, b, c, bc, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(add, [b, c, bc], false);
                rb.QueryTable(mul, [a, bc, e], false);
                var ab = rb.Lookup(mul, [a, b], static () => "Mul");
                var ac = rb.Lookup(mul, [a, c], static () => "Mul");
                rb.Set(add, [ab, ac, e]);
                rules.Add(rb.Build());
            }

            var all = rules.ToArray();
            void Sizes(int adds, int muls, int nums)
            {
                Assert.Equal(adds, p.Size(add));
                Assert.Equal(muls, p.Size(mul));
                Assert.Equal(nums, p.Size(num));
            }

            Value N(long v) => p.Term(num, p.Int(v));
            Value? TryN(long v) => p.TryTerm(num, p.Int(v));

            // (let $e (Mul (Num 2) (Add (Num 3) (Num 4)))) (run 1)
            p.Term(mul, N(2), p.Term(add, N(3), N(4)));
            Assert.Equal(1, p.Run(all, 1));
            Sizes(3, 3, 3);

            // (push) (union (Num 3) (Num 4)) (run 2) (check (= (Add (Num 3) (Num 3)) (Add (Num 4) (Num 4))))
            p.Push();
            p.Union(N(3), N(4));
            Assert.Equal(1, p.Run(all, 2));
            Sizes(2, 2, 3);
            Assert.True(p.Equal(p.TryTerm(add, TryN(3), TryN(3)), p.TryTerm(add, TryN(4), TryN(4))));

            // (push) (let $g (Mul (Num 5) (Add (Num 2) (Num 3)))) (run 10)
            // (check (= $g (Add (Mul (Num 5) (Num 2)) (Mul (Num 5) (Num 4)))))
            p.Push();
            Assert.Equal(2, p.Depth);
            var five = N(5);
            var gg = p.Term(mul, five, p.Term(add, N(2), N(3)));
            Assert.Equal(3, p.Run(all, 10));
            Sizes(6, 5, 4);
            Assert.True(p.Equal(gg, p.TryTerm(add, p.TryTerm(mul, TryN(5), TryN(2)), p.TryTerm(mul, TryN(5), TryN(4)))));

            // (pop) (run 1)
            p.Pop();
            Sizes(2, 2, 3);
            Assert.Equal(1, p.Run(all, 1));
            Sizes(2, 2, 3);

            // (pop) (fail (check (= (Num 3) (Num 4)))) (run 1)
            p.Pop();
            Sizes(3, 3, 3);
            Assert.False(p.Equal(TryN(3), TryN(4)));
            Assert.Null(TryN(5));
            Assert.Equal(1, p.Run(all, 1));
            Sizes(4, 3, 3);

            p.AssertAllInherit(all);
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// The sizes egglog prints at the root of <c>calc.egg</c> around every push: one row in each nullary
        /// constructor, three in <c>g*</c> and none in <c>inv</c>.
        /// </summary>
        static void AssertRootSizes(EggPushPopProgram p, FunctionId mul, FunctionId inv, params FunctionId[] nullary)
        {
            Assert.Equal(0, p.Depth);
            Assert.Equal(3, p.Size(mul));
            Assert.Equal(0, p.Size(inv));
            foreach (var f in nullary)
                Assert.Equal(1, p.Size(f));
        }

    }

}
