using System;
using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// egglog's push/pop programs under <c>ext/egglog/tests/web-demo</c>, translated by hand like
    /// <see cref="EggPushPopTests"/>, for the programs that use only what the bridge has: i64 and string values,
    /// constructors, relations, unions, panics and pure primitives.
    /// </summary>
    /// <remarks>
    /// <see cref="EggPushPopTests"/> covers the push/pop programs at the top of <c>ext/egglog/tests</c>; this class
    /// covers the eleven programs with a <c>(push)</c> under <c>tests/web-demo</c>. Most need sorts the bridge does not
    /// have, or eq-sort containers in children; <c>bdd.egg</c>, <c>push-pop.egg</c>, <c>array.egg</c> and
    /// <c>cyk.egg</c> do not (strings are interned base values, <see cref="StringCodec"/>). Their expected values are
    /// their own <c>check</c> and <c>(fail (check ...))</c> commands, which egglog's test suite runs; <c>bdd.egg</c> is
    /// also compared with the same commands run at a fresh root, without the push.
    /// <para>
    /// Not translated. <c>lambda.egg</c>, <c>typeinfer.egg</c> and <c>fusion.egg</c> hold <c>Set</c> containers of
    /// e-classes inside a push: they wait on eq-sort containers in children, which children refuse, and would be
    /// translated with <see cref="SetContainer"/> once children have them. The rest are out of scope. <c>herbie.egg</c>
    /// and <c>herbie-tutorial.egg</c> need <c>BigRat</c> primitives, which the bridge has none of (its values would use the
    /// library's <c>BigRationalCodec</c>);
    /// <c>herbie.egg</c> is 196 rules to translate by hand, and the tutorial's oracle is three <c>check</c> commands.
    /// <c>multiset.egg</c> needs <c>MultiSet</c> and <c>UnstableFn</c> function values, which the library does not
    /// offer. <c>math.egg</c> needs only <c>f64</c> (<see cref="DoubleCodec"/>) and strings, but nothing runs at its
    /// root before its push blocks, so each push starts from a root that has run nothing and tests what the root's own
    /// tests cover; its one feature specific to children, subsumption and deletion by rules inside a push, is covered
    /// by <see cref="ChildSubsumeAndDeleteTests"/>. A translation would have to freeze the root before the first push,
    /// since its <c>delete</c> rule runs inside a push and a child deletes a shared table's rows only under a frozen
    /// parent.
    /// </para>
    /// </remarks>
    public class EggPushPopWebDemoTests
    {

        /// <summary>
        /// <c>ext/egglog/tests/web-demo/bdd.egg</c>: binary decision diagrams over <c>ITE</c>, with rewrites for
        /// <c>bddand</c>, <c>bddor</c>, <c>bddnot</c> and <c>bddxor</c>; one push that builds thirteen formulas, runs
        /// 30 iterations and checks their normal forms, then a pop that restores the root.
        /// </summary>
        [Fact]
        public void Bdd()
        {
            var inChild = RunBdd(push: true);
            var atRoot = RunBdd(push: false);

            // A child behaves as a copy of its parent: the push's sizes and iteration count equal those of a fresh root
            // running the same commands.
            Assert.Equal(atRoot, inChild);
        }

        /// <summary>
        /// <c>ext/egglog/tests/web-demo/push-pop.egg</c>: a nullary function with a <c>max</c> merge, set to 1 at the
        /// root and to 2 inside a push; the pop restores 1.
        /// </summary>
        [Fact]
        public void PushPop()
        {
            var p = new EggPushPopProgram();
            var g = p.EGraph;

            // (function foo () i64 :merge (max old new))
            var max = p.Primitive((state, args) => p.Int(Math.Max(state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0])), state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1])))).Rep);
            var foo = g.AddTable(new FunctionConfig
            {
                Schema = [p.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(max, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "foo",
            });

            // (set (foo) 1) (check (= (foo) 1))
            p.Current.StageRow(foo, [p.Int(1)]);
            p.Current.FlushUpdates();
            Assert.Equal(p.Int(1), p.Current.LookupId(foo, []));

            // (push) (set (foo) 2) (check (= (foo) 2)) (pop)
            p.Push();
            p.Current.StageRow(foo, [p.Int(2)]);
            p.Current.FlushUpdates();
            Assert.Equal(p.Int(2), p.Current.LookupId(foo, []));
            Assert.Equal(1, p.Size(foo));
            p.Pop();

            // (check (= (foo) 1))
            Assert.Equal(p.Int(1), p.Current.LookupId(foo, []));
            Assert.Equal(1, p.Size(foo));
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// <c>ext/egglog/tests/web-demo/array.egg</c>: arrays as <c>select</c>/<c>store</c> terms over arithmetic, with
        /// a symmetric <c>neq</c> relation and a rule that panics if <c>neq</c> ever relates a term to itself; one push
        /// that builds four terms, runs 5 iterations and checks them, then a pop that restores the root.
        /// </summary>
        [Fact]
        public void Array()
        {
            var p = new EggPushPopProgram();
            var g = p.EGraph;
            var str = ColumnTy.Base(g.BaseValues.RegisterType<StringCodec, string>());
            Value S(string s) => g.BaseValues.Get<StringCodec, string>(s);

            // (datatype Math (Num i64) (Var String)) (datatype Array (Const i64) (AVar String))
            var num = p.Constructor("Num", p.I64);
            var variable = p.Constructor("Var", str);
            p.Constructor("Const", p.I64);
            var avar = p.Constructor("AVar", str);
            var add = p.Constructor("add", ColumnTy.Id, ColumnTy.Id);
            var select = p.Constructor("select", ColumnTy.Id, ColumnTy.Id);
            var store = p.Constructor("store", ColumnTy.Id, ColumnTy.Id, ColumnTy.Id);
            var neq = p.Relation("neq", ColumnTy.Id, ColumnTy.Id);
            var unit = RuleEntry.Const(p.Unit, p.UnitType);
            var zero = RuleEntry.Const(p.Int(0), p.I64);
            var differs = p.Primitive((state, args) => args[0] != args[1] ? p.Unit.Rep : null);
            var plus = p.Primitive((state, args) => p.Int(state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0])) + state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1]))).Rep);
            var rules = new List<EGraphRuleId>();
            static RuleVariable Id(EGraphRuleBuilder rb) => rb.NewVar(ColumnTy.Id);

            // (rule ((neq x y)) ((neq y x)))
            {
                var rb = g.NewRule("neq-symmetric", true);
                var (x, y) = (Id(rb), Id(rb));
                rb.QueryTable(neq, [x, y, rb.NewVar(p.UnitType)], false);
                rb.Set(neq, [y, x, unit]);
                rules.Add(rb.Build());
            }

            // (rule ((neq x x)) ((panic "query (neq x x) found something equal to itself")))
            {
                var rb = g.NewRule("neq-irreflexive", true);
                var x = Id(rb);
                rb.QueryTable(neq, [x, x, rb.NewVar(p.UnitType)], false);
                rb.Panic("query (neq x x) found something equal to itself");
                rules.Add(rb.Build());
            }

            // (rule ((neq x y) (= (add x z) e)) ((neq (add x z) (add y z))))
            {
                var rb = g.NewRule("neq-add", true);
                var (x, y, z, e) = (Id(rb), Id(rb), Id(rb), Id(rb));
                rb.QueryTable(neq, [x, y, rb.NewVar(p.UnitType)], false);
                rb.QueryTable(add, [x, z, e], false);
                var xz = rb.Lookup(add, [x, z], static () => "add");
                var yz = rb.Lookup(add, [y, z], static () => "add");
                rb.Set(neq, [xz, yz, unit]);
                rules.Add(rb.Build());
            }

            // (rule ((= (add x (Num i)) e) (!= i 0)) ((neq e x)))
            {
                var rb = g.NewRule("neq-offset", true);
                var (x, n, e, i) = (Id(rb), Id(rb), Id(rb), rb.NewVar(p.I64));
                rb.QueryTable(num, [i, n], false);
                rb.QueryTable(add, [x, n, e], false);
                rb.QueryPrim(differs, [i, zero, unit], p.UnitType);
                rb.Set(neq, [e, x, unit]);
                rules.Add(rb.Build());
            }

            // (rule ((= (Num a) n1) (= (Num b) n2) (!= a b)) ((neq n1 n2)))
            {
                var rb = g.NewRule("neq-num", true);
                var (a, b, n1, n2) = (rb.NewVar(p.I64), rb.NewVar(p.I64), Id(rb), Id(rb));
                rb.QueryTable(num, [a, n1], false);
                rb.QueryTable(num, [b, n2], false);
                rb.QueryPrim(differs, [a, b, unit], p.UnitType);
                rb.Set(neq, [n1, n2, unit]);
                rules.Add(rb.Build());
            }

            // (rewrite (select (store mem i e) i) e)
            {
                var rb = g.NewRule("select-store", true);
                var (mem, i, e, s, r) = (Id(rb), Id(rb), Id(rb), Id(rb), Id(rb));
                rb.QueryTable(store, [mem, i, e, s], false);
                rb.QueryTable(select, [s, i, r], false);
                rb.Union(r, e);
                rules.Add(rb.Build());
            }

            // (rule ((= (select (store mem i1 e) i2) e1) (neq i1 i2)) ((union (select mem i2) e1)))
            {
                var rb = g.NewRule("select-store-other", true);
                var (mem, i1, e, s, i2, e1) = (Id(rb), Id(rb), Id(rb), Id(rb), Id(rb), Id(rb));
                rb.QueryTable(store, [mem, i1, e, s], false);
                rb.QueryTable(select, [s, i2, e1], false);
                rb.QueryTable(neq, [i1, i2, rb.NewVar(p.UnitType)], false);
                rb.Set(select, [mem, i2, e1]);
                rules.Add(rb.Build());
            }

            // (rewrite (store (store mem i e1) i e2) (store mem i e2))
            {
                var rb = g.NewRule("store-store", true);
                var (mem, i, e1, s1, e2, s2) = (Id(rb), Id(rb), Id(rb), Id(rb), Id(rb), Id(rb));
                rb.QueryTable(store, [mem, i, e1, s1], false);
                rb.QueryTable(store, [s1, i, e2, s2], false);
                rb.Set(store, [mem, i, e2, s2]);
                rules.Add(rb.Build());
            }

            // (rule ((= (store (store mem i2 e2) i1 e1) mem1) (neq i1 i2)) ((union (store (store mem i1 e1) i2 e2) mem1)))
            {
                var rb = g.NewRule("store-swap", true);
                var (mem, i2, e2, s1, i1, e1, swapped) = (Id(rb), Id(rb), Id(rb), Id(rb), Id(rb), Id(rb), Id(rb));
                rb.QueryTable(store, [mem, i2, e2, s1], false);
                rb.QueryTable(store, [s1, i1, e1, swapped], false);
                rb.QueryTable(neq, [i1, i2, rb.NewVar(p.UnitType)], false);
                var inner = rb.Lookup(store, [mem, i1, e1], static () => "store");
                rb.Set(store, [inner, i2, e2, swapped]);
                rules.Add(rb.Build());
            }

            // (rewrite (add x y) (add y x))
            {
                var rb = g.NewRule("add-comm", true);
                var (x, y, e) = (Id(rb), Id(rb), Id(rb));
                rb.QueryTable(add, [x, y, e], false);
                rb.Set(add, [y, x, e]);
                rules.Add(rb.Build());
            }

            // (rewrite (add (add x y) z) (add x (add y z)))
            {
                var rb = g.NewRule("add-assoc", true);
                var (x, y, xy, z, e) = (Id(rb), Id(rb), Id(rb), Id(rb), Id(rb));
                rb.QueryTable(add, [x, y, xy], false);
                rb.QueryTable(add, [xy, z, e], false);
                var yz = rb.Lookup(add, [y, z], static () => "add");
                rb.Set(add, [x, yz, e]);
                rules.Add(rb.Build());
            }

            // (rewrite (add (Num x) (Num y)) (Num (+ x y)))
            {
                var rb = g.NewRule("add-fold", true);
                var (x, nx, y, ny, e) = (rb.NewVar(p.I64), Id(rb), rb.NewVar(p.I64), Id(rb), Id(rb));
                rb.QueryTable(num, [x, nx], false);
                rb.QueryTable(num, [y, ny], false);
                rb.QueryTable(add, [nx, ny, e], false);
                var sum = rb.CallExternalFunc(plus, [x, y], p.I64, static () => "+");
                rb.Set(num, [sum, e]);
                rules.Add(rb.Build());
            }

            // (rewrite (add x (Num 0)) x)
            {
                var rb = g.NewRule("add-zero", true);
                var (x, n, e) = (Id(rb), Id(rb), Id(rb));
                rb.QueryTable(num, [zero, n], false);
                rb.QueryTable(add, [x, n, e], false);
                rb.Union(e, x);
                rules.Add(rb.Build());
            }

            var all = rules.ToArray();
            var functions = new[] { num, variable, avar, add, select, store, neq };

            // (push)
            p.Push();
            Value N(long v) => p.Term(num, p.Int(v));
            var r1 = p.Term(variable, S("r1"));
            var r2 = p.Term(variable, S("r2"));
            var r3 = p.Term(variable, S("r3"));
            var mem1 = p.Term(avar, S("mem1"));
            p.Fact(neq, r1, r2);
            p.Fact(neq, r2, r3);
            p.Fact(neq, r1, r3);
            var test1 = p.Term(select, p.Term(store, mem1, r1, N(42)), r1);
            var test2 = p.Term(select, p.Term(store, mem1, r1, N(42)), p.Term(add, r1, N(17)));
            var test3 = p.Term(select, p.Term(store, p.Term(store, mem1, p.Term(add, r1, r2), N(1)), p.Term(add, r2, r1), N(2)), p.Term(add, r1, r3));
            var test4 = p.Term(add, N(1), p.Term(add, p.Term(add, N(1), p.Term(add, N(1), r1)), N(-3)));

            // (run 5)
            p.Run(all, 5);
            p.Current.CheckInvariants();
            Value? TryN(long v) => p.TryTerm(num, p.Int(v));
            bool Neq(Value? a, Value? b) => a is { } x && b is { } y && p.Current.ContainsKey(neq, [p.Current.Find(x), p.Current.Find(y)]);
            Assert.True(p.Equal(test1, TryN(42)));
            Assert.True(Neq(r1, r2));
            Assert.True(Neq(r1, p.TryTerm(add, r1, TryN(17))));
            Assert.True(p.Equal(test2, p.TryTerm(select, mem1, p.TryTerm(add, r1, TryN(17)))));
            Assert.True(p.Equal(test3, p.TryTerm(select, mem1, p.TryTerm(add, r1, r3))));
            Assert.True(p.Equal(test4, r1));

            // (pop): the root holds nothing of the push.
            p.Pop();
            foreach (var function in functions)
                Assert.Equal(0, p.Size(function));

            p.AssertAllInherit(all);
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// <c>ext/egglog/tests/web-demo/cyk.egg</c>: the CYK parsing algorithm as rules over a grammar (<c>Prod</c>,
        /// <c>End</c>) and a sentence (<c>getString</c>, a <c>:no-merge</c> function written inside the pushes); three
        /// blocks, the last two nested under a push that holds their grammar, each running to saturation and checking
        /// which spans parse as which non-terminals. (The <c>extract</c> commands only print and are not compared.)
        /// </summary>
        [Fact]
        public void Cyk()
        {
            var p = new EggPushPopProgram();
            var g = p.EGraph;
            var str = ColumnTy.Base(g.BaseValues.RegisterType<StringCodec, string>());
            Value S(string s) => g.BaseValues.Get<StringCodec, string>(s);

            // (datatype term (Term String)) (datatype nonterm (NonTerm String))
            // (datatype tree (NT String tree tree) (T String String))
            p.Constructor("Term", str);
            var nonTerm = p.Constructor("NonTerm", str);
            var nt = p.Constructor("NT", str, ColumnTy.Id, ColumnTy.Id);
            var leaf = p.Constructor("T", str, str);

            // (function getString (i64) String :no-merge)
            var getString = g.AddTable(new FunctionConfig { Schema = [p.I64, str], Default = DefaultVal.Fail, Merge = MergeFn.AssertEq, Name = "getString" });
            var prod = p.Relation("Prod", ColumnTy.Id, ColumnTy.Id, ColumnTy.Id);
            var end = p.Relation("End", ColumnTy.Id, str);
            var pRel = p.Relation("P", p.I64, p.I64, ColumnTy.Id);
            var b = p.Constructor("B", p.I64, p.I64, ColumnTy.Id);
            var unit = RuleEntry.Const(p.Unit, p.UnitType);
            var one = RuleEntry.Const(p.Int(1), p.I64);
            var plus = p.Primitive((state, args) => p.Int(state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0])) + state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1]))).Rep);
            var rules = new List<EGraphRuleId>();

            // (rule ((End (NonTerm a) s) (= s (getString pos)))
            //       ((P 1 pos (NonTerm a)) (union (B 1 pos (NonTerm a)) (T a s))))
            {
                var rb = g.NewRule("end", true);
                var (a, n, s, pos) = (rb.NewVar(str), rb.NewVar(ColumnTy.Id), rb.NewVar(str), rb.NewVar(p.I64));
                rb.QueryTable(nonTerm, [a, n], false);
                rb.QueryTable(end, [n, s, rb.NewVar(p.UnitType)], false);
                rb.QueryTable(getString, [pos, s], false);
                rb.Set(pRel, [one, pos, n, unit]);
                var tree = rb.Lookup(leaf, [a, s], static () => "T");
                rb.Set(b, [one, pos, n, tree]);
                rules.Add(rb.Build());
            }

            // (rule ((Prod (NonTerm a) (NonTerm b) (NonTerm c)) (P p1 s (NonTerm b)) (P p2 (+ s p1) (NonTerm c)))
            //       ((P (+ p1 p2) s (NonTerm a))))
            {
                var rb = g.NewRule("prod", true);
                var (na, nb, nc) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                var (p1, s, p2, sp) = (rb.NewVar(p.I64), rb.NewVar(p.I64), rb.NewVar(p.I64), rb.NewVar(p.I64));
                rb.QueryTable(nonTerm, [rb.NewVar(str), na], false);
                rb.QueryTable(nonTerm, [rb.NewVar(str), nb], false);
                rb.QueryTable(nonTerm, [rb.NewVar(str), nc], false);
                rb.QueryTable(prod, [na, nb, nc, rb.NewVar(p.UnitType)], false);
                rb.QueryTable(pRel, [p1, s, nb, rb.NewVar(p.UnitType)], false);
                rb.QueryTable(pRel, [p2, sp, nc, rb.NewVar(p.UnitType)], false);
                rb.QueryPrim(plus, [s, p1, sp], p.I64);
                var length = rb.CallExternalFunc(plus, [p1, p2], p.I64, static () => "+");
                rb.Set(pRel, [length, s, na, unit]);
                rules.Add(rb.Build());
            }

            // (rule ((Prod (NonTerm a) (NonTerm b) (NonTerm c)) (= f1 (B p1 s (NonTerm b))) (= f2 (B p2 (+ s p1) (NonTerm c))))
            //       ((union (B (+ p1 p2) s (NonTerm a)) (NT a f1 f2))))
            {
                var rb = g.NewRule("tree", true);
                var (a, na, nb, nc) = (rb.NewVar(str), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                var (p1, s, p2, sp, f1, f2) = (rb.NewVar(p.I64), rb.NewVar(p.I64), rb.NewVar(p.I64), rb.NewVar(p.I64), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(nonTerm, [a, na], false);
                rb.QueryTable(nonTerm, [rb.NewVar(str), nb], false);
                rb.QueryTable(nonTerm, [rb.NewVar(str), nc], false);
                rb.QueryTable(prod, [na, nb, nc, rb.NewVar(p.UnitType)], false);
                rb.QueryTable(b, [p1, s, nb, f1], false);
                rb.QueryTable(b, [p2, sp, nc, f2], false);
                rb.QueryPrim(plus, [s, p1, sp], p.I64);
                var length = rb.CallExternalFunc(plus, [p1, p2], p.I64, static () => "+");
                var node = rb.Lookup(nt, [a, f1, f2], static () => "NT");
                rb.Set(b, [length, s, na, node]);
                rules.Add(rb.Build());
            }

            var all = rules.ToArray();
            Value Nt(string name) => p.Term(nonTerm, S(name));
            void Sentence(params string[] words)
            {
                for (var i = 0; i < words.Length; i++)
                    p.Current.StageRow(getString, [p.Int(i + 1), S(words[i])]);

                p.Current.FlushUpdates();
            }

            void Prod(string a, string bb, string c) => p.Fact(prod, Nt(a), Nt(bb), Nt(c));
            void End(string a, string word) => p.Fact(end, Nt(a), S(word));
            bool Parses(long length, string name) => p.TryTerm(nonTerm, S(name)) is { } n && p.Current.ContainsKey(pRel, [p.Int(length), p.Int(1), p.Current.Find(n)]);

            // Block 1.
            p.Push();
            Sentence("she", "eats", "a", "fish", "with", "a", "fork");
            Prod("S", "NP", "VP");
            Prod("VP", "VP", "PP");
            Prod("VP", "V", "NP");
            End("VP", "eats");
            Prod("PP", "P", "NP");
            Prod("NP", "DET", "N");
            End("NP", "she");
            End("V", "eats");
            End("P", "with");
            End("N", "fish");
            End("N", "fork");
            End("DET", "a");
            Assert.True(p.Run(all, 100) < 100);
            p.Current.CheckInvariants();
            Assert.True(Parses(7, "S"));
            Assert.False(Parses(7, "VP"));
            Assert.False(Parses(7, ""));
            p.Pop();
            Assert.Equal(0, p.Size(pRel));
            Assert.Equal(0, p.Size(getString));

            // Block 2: a grammar in a push, and two sentences in pushes under it.
            p.Push();
            Prod("S", "A", "B");
            Prod("S", "B", "C");
            Prod("A", "B", "A");
            End("A", "a");
            Prod("B", "C", "C");
            End("B", "b");
            Prod("C", "A", "B");
            End("C", "a");
            var grammar = p.Size(prod);

            p.Push();
            Sentence("a", "b", "a", "a", "b");
            Assert.True(p.Run(all, 100) < 100);
            p.Current.CheckInvariants();
            Assert.True(Parses(5, "S"));
            Assert.False(Parses(5, "B"));
            p.Pop();

            Assert.Equal(grammar, p.Size(prod));
            Assert.Equal(0, p.Size(pRel));
            p.Push();
            Sentence("a", "a", "a", "a", "a");
            Assert.True(p.Run(all, 100) < 100);
            p.Current.CheckInvariants();
            Assert.True(Parses(5, "S"));
            Assert.True(Parses(5, "A"));
            Assert.False(Parses(5, "B"));
            Assert.False(Parses(5, ""));
            Assert.False(Parses(5, "unrelated"));
            p.Pop();

            p.Pop();
            p.AssertAllInherit(all);
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// Runs <c>bdd.egg</c>, inside a push or at the root, asserts its checks, and returns the iterations its
        /// <c>(run 30)</c> took followed by the sizes of <c>ITE</c>, <c>bddand</c>, <c>bddor</c>, <c>bddnot</c> and
        /// <c>bddxor</c> before the pop.
        /// </summary>
        static int[] RunBdd(bool push)
        {
            var p = new EggPushPopProgram();
            var g = p.EGraph;

            // (datatype BDD (ITE i64 BDD BDD))
            var ite = p.Constructor("ITE", p.I64, ColumnTy.Id, ColumnTy.Id);

            // (constructor TrueConst () BDD) (let $True (TrueConst)) (constructor FalseConst () BDD) (let $False (FalseConst))
            var trueConst = p.Constructor("TrueConst");
            var t = p.Term(trueConst);
            var falseConst = p.Constructor("FalseConst");
            var f = p.Term(falseConst);

            var and = p.Constructor("bddand", ColumnTy.Id, ColumnTy.Id);
            var or = p.Constructor("bddor", ColumnTy.Id, ColumnTy.Id);
            var not = p.Constructor("bddnot", ColumnTy.Id);
            var xor = p.Constructor("bddxor", ColumnTy.Id, ColumnTy.Id);

            // egglog's (< n m): unit when it holds, nothing otherwise (a failed filter drops the match).
            var less = p.Primitive((state, args) => state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0])) < state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1])) ? p.Unit.Rep : null);
            var unit = RuleEntry.Const(p.Unit, p.UnitType);
            var rules = new List<EGraphRuleId>();

            // (rewrite (ITE n a a) a)
            {
                var rb = g.NewRule("ite-same", true);
                var (n, a, e) = (rb.NewVar(p.I64), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(ite, [n, a, a, e], false);
                rb.Union(e, a);
                rules.Add(rb.Build());
            }

            // (rewrite (op x y) (op y x)) for bddand, bddor and bddxor.
            foreach (var (op, name) in new[] { (and, "and"), (or, "or"), (xor, "xor") })
            {
                var rb = g.NewRule(name + "-comm", true);
                var (x, y, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(op, [x, y, e], false);
                rb.Set(op, [y, x, e]);
                rules.Add(rb.Build());
            }

            // (rewrite (bddand $False n) $False) and (rewrite (bddor $True n) $True): an absorbing constant.
            foreach (var (op, absorbing, name) in new[] { (and, falseConst, "and-false"), (or, trueConst, "or-true") })
            {
                var rb = g.NewRule(name, true);
                var (c, n, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(absorbing, [c], false);
                rb.QueryTable(op, [c, n, e], false);
                rb.Union(e, c);
                rules.Add(rb.Build());
            }

            // (rewrite (bddand $True x) x), (rewrite (bddor $False x) x) and (rewrite (bddxor $False x) x): an identity.
            foreach (var (op, identity, name) in new[] { (and, trueConst, "and-true"), (or, falseConst, "or-false"), (xor, falseConst, "xor-false") })
            {
                var rb = g.NewRule(name, true);
                var (c, x, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(identity, [c], false);
                rb.QueryTable(op, [c, x, e], false);
                rb.Union(e, x);
                rules.Add(rb.Build());
            }

            // (rewrite (op (ITE n a1 a2) (ITE m b1 b2)) (ITE n (op a1 (ITE m b1 b2)) (op a2 (ITE m b1 b2))) :when ((< n m)))
            // and (rewrite (op (ITE n a1 a2) (ITE n b1 b2)) (ITE n (op a1 b1) (op a2 b2))) for bddand, bddor and bddxor.
            foreach (var (op, name) in new[] { (and, "and"), (or, "or"), (xor, "xor") })
            {
                var rb = g.NewRule(name + "-ite-less", true);
                var (n, a1, a2, l) = (rb.NewVar(p.I64), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                var (m, b1, b2, r, e) = (rb.NewVar(p.I64), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(ite, [n, a1, a2, l], false);
                rb.QueryTable(ite, [m, b1, b2, r], false);
                rb.QueryTable(op, [l, r, e], false);
                rb.QueryPrim(less, [n, m, unit], p.UnitType);
                var right = rb.Lookup(ite, [m, b1, b2], static () => "ITE");
                var left = rb.Lookup(op, [a1, right], () => name);
                var other = rb.Lookup(op, [a2, right], () => name);
                rb.Set(ite, [n, left, other, e]);
                rules.Add(rb.Build());

                rb = g.NewRule(name + "-ite-same-var", true);
                (n, a1, a2, l) = (rb.NewVar(p.I64), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                (b1, b2, r, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(ite, [n, a1, a2, l], false);
                rb.QueryTable(ite, [n, b1, b2, r], false);
                rb.QueryTable(op, [l, r, e], false);
                var x1 = rb.Lookup(op, [a1, b1], () => name);
                var x2 = rb.Lookup(op, [a2, b2], () => name);
                rb.Set(ite, [n, x1, x2, e]);
                rules.Add(rb.Build());
            }

            // (rewrite (bddnot $True) $False) and (rewrite (bddnot $False) $True)
            foreach (var (from, to, name) in new[] { (trueConst, falseConst, "not-true"), (falseConst, trueConst, "not-false") })
            {
                var rb = g.NewRule(name, true);
                var (c, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(from, [c], false);
                rb.QueryTable(not, [c, e], false);
                var other = rb.Lookup(to, [], () => name);
                rb.Union(e, other);
                rules.Add(rb.Build());
            }

            // (rewrite (bddnot (ITE n a1 a2)) (ITE n (bddnot a1) (bddnot a2)))
            {
                var rb = g.NewRule("not-ite", true);
                var (n, a1, a2, x, e) = (rb.NewVar(p.I64), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(ite, [n, a1, a2, x], false);
                rb.QueryTable(not, [x, e], false);
                var y1 = rb.Lookup(not, [a1], static () => "bddnot");
                var y2 = rb.Lookup(not, [a2], static () => "bddnot");
                rb.Set(ite, [n, y1, y2, e]);
                rules.Add(rb.Build());
            }

            // (rewrite (bddxor $True n) (bddnot n))
            {
                var rb = g.NewRule("xor-true", true);
                var (c, n, e) = (rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id));
                rb.QueryTable(trueConst, [c], false);
                rb.QueryTable(xor, [c, n, e], false);
                rb.Set(not, [n, e]);
                rules.Add(rb.Build());
            }

            var all = rules.ToArray();
            var functions = new[] { ite, and, or, not, xor };

            // (push)
            if (push)
                p.Push();

            Value Ite(long n, Value a, Value b) => p.Term(ite, p.Int(n), a, b);
            Value And(Value a, Value b) => p.Term(and, a, b);
            Value Or(Value a, Value b) => p.Term(or, a, b);
            Value Not(Value a) => p.Term(not, a);
            Value Xor(Value a, Value b) => p.Term(xor, a, b);

            var v0 = Ite(0, t, f);
            var v1 = Ite(1, t, f);
            var v2 = Ite(2, t, f);
            var t0 = Not(Not(v0));
            var t1 = Or(v0, Not(v0));
            var t2 = And(v0, Not(v0));
            var t3 = And(v0, v0);
            var t4 = Or(v0, v0);
            var t5 = Xor(Not(v0), v0);
            var t6 = And(Or(v1, v2), v2);
            var t7a = Xor(Not(v0), v1);
            var t7b = Xor(v0, Not(v1));
            var t7c = Not(Xor(v0, v1));
            var t8 = And(v1, v2);
            var t9 = And(Not(v1), And(Not(v0), Xor(v0, v1)));
            var t10 = Or(Not(v1), Or(Not(v0), Xor(v0, Not(v1))));

            // (run 30)
            var iterations = p.Run(all, 30);
            p.Current.CheckInvariants();

            Assert.True(p.Equal(t0, v0), "bddnot cancels");
            Assert.True(p.Equal(t1, t));
            Assert.True(p.Equal(t2, f));
            Assert.True(p.Equal(t3, v0));
            Assert.True(p.Equal(t4, v0));
            Assert.True(p.Equal(t5, t));
            Assert.True(p.Equal(t6, v2));
            Assert.True(p.Equal(t7a, t7b));
            Assert.True(p.Equal(t7a, t7c));
            Assert.True(p.Equal(t8, p.TryTerm(ite, p.Int(1), p.TryTerm(ite, p.Int(2), t, f), f)));
            Assert.True(p.Equal(t9, f));
            Assert.True(p.Equal(t10, t));

            var result = new int[functions.Length + 1];
            result[0] = iterations;
            for (var i = 0; i < functions.Length; i++)
                result[i + 1] = p.Size(functions[i]);

            p.AssertAllInherit(all);
            if (push == false)
                return result;

            // (pop): the root holds the two nullary constants and nothing else.
            p.Pop();
            Assert.Equal(1, p.Size(trueConst));
            Assert.Equal(1, p.Size(falseConst));
            foreach (var function in functions)
                Assert.Equal(0, p.Size(function));

            g.Root.CheckInvariants();
            return result;
        }

    }

}
