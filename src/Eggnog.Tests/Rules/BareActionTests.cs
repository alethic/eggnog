using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// egglog's bare calls (<see cref="Fact.Exists"/>): a check in a body, a query or a host read, and in a head or a host
    /// write an action run for its effect alone, a <c>let</c> of a value never used (src/core.rs:765-770). Each case's
    /// egglog form ran on egglog's binary at 5e655cae as the comment says.
    /// </summary>
    public sealed class BareActionTests
    {

        [Fact]
        public void AHostActionMakesATermRunsAPrimitiveAndReadsAFunction()
        {
            // (datatype Math (Num i64)) (function f (i64) i64 :no-merge) (set (f 1) 10)
            // (f 1) (Num 4) (check (Num 4)) (vec-get (vec-of 1 2) 1) (fail (vec-get (vec-of 1 2) 5)) (fail (f 2))
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var f = graph.DefineFunction<long, long>("f");
            graph.Values.RegisterVec<long>();
            var root = graph.Root;
            root.Assume(f.Of(1L).Is(10L));

            root.Assume(Fact.Exists(f.Of(1L)));
            root.Assume(Fact.Exists(num.Of(4L)));
            Assert.True(root.Check(Fact.Exists(num.Of(4L))));
            var get = Primitives.VecGet<long>();
            var two = Primitives.VecOf<long>().Of(1L, 2L);
            root.Assume(Fact.Exists(get.Of(two, 1L)));
            Assert.ThrowsAny<Exception>(() => root.Assume(Fact.Exists(get.Of(two, 5L))));
            Assert.ThrowsAny<Exception>(() => root.Assume(Fact.Exists(f.Of(2L))));
            Assert.False(root.IsFaulted);
        }

        [Fact]
        public void AHeadActionMakesATermAndRunsAWritePrimitive()
        {
            // (datatype Math (Num i64)) (relation R (i64)) (R 3) (rule ((R x)) ((Num x))) (run 1) (check (Num 3))
            var math = new EGraph();
            math.DefineSort<Node>("Math");
            var num = math.DefineConstructor<long, Node>("Num");
            var r3 = math.DefineRelation<long>("R");
            math.Root.Assume(r3.Of(3L));
            var made = math.DefineRuleset("rules");
            var make = made.DefineRule("make", r =>
            {
                var x = r.Var<long>("x");
                r.When(r3.Of(x));
                r.Then(Fact.Exists(num.Of(x)));
            });
            Assert.Equal("(rule ((R x)) ((Num x)) :ruleset rules)", make.ToString());
            math.Root.Run(made);
            Assert.True(math.Root.Check(Fact.Exists(num.Of(3L))));

            // python_array_optimize.egg's ((vec-union _vs _vs2)): a head unions two vectors' classes.
            var graph = new EGraph();
            graph.DefineSort<Node>("X");
            var a = graph.DefineConstructor<Node>("a");
            var b = graph.DefineConstructor<Node>("b");
            graph.Values.RegisterVec<Node>();
            var p = graph.DefineFunction<Vec<Node>>("p");
            var q = graph.DefineFunction<Vec<Node>>("q");
            graph.Root.Assume(p.Of().Is(Primitives.VecOf<Node>().Of(a.Of())), q.Of().Is(Primitives.VecOf<Node>().Of(b.Of())));
            var rules = graph.DefineRuleset("rules");
            var union = rules.DefineRule("union", r =>
            {
                var (x, y) = r.Vars<Vec<Node>, Vec<Node>>("x", "y");
                r.When(x.Is(p.Of()), y.Is(q.Of()));
                r.Then(Fact.Exists(Primitives.VecUnion<Node>().Of(x, y)));
            });
            Assert.Equal("(rule ((= x (p)) (= y (q))) ((vec-union x y)) :ruleset rules)", union.ToString());
            graph.Root.Run(rules);
            Assert.True(graph.Root.AreEqual(graph.Root.Make(a.Of()), b.Of()));
        }

        [Fact]
        public void AHeadActionReadsNoFunctionAndFailsWithoutAResult()
        {
            // (rule ((R x)) ((f x))) is "Value lookup of non-constructor function f in rule is disallowed." in egglog.
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var keys = t.Graph.DefineRelation<long>("R");
            var read = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("read", r =>
            {
                var x = r.Var<long>("x");
                r.When(keys.Of(x));
                r.Then(Fact.Exists(f.Of(x)));
            }));
            Assert.Contains("actions do not read", read.Message, StringComparison.Ordinal);

            // A primitive's call with no result fails the rule, as egglog's panics.
            t.Graph.Values.RegisterVec<long>();
            t.Rules.DefineRule("get", r =>
            {
                var x = r.Var<long>("x");
                r.When(keys.Of(x));
                r.Then(Fact.Exists(Primitives.VecGet<long>().Of(Primitives.VecOf<long>().Of(x), 5L)));
            });
            t.Root.Assume(keys.Of(1L));
            Assert.Throws<RuleEvaluationException>(() => t.Root.Run(t.Rules));
        }

        [Fact]
        public void ACallInABodyOrACheckHoldsWhereItHasAResult()
        {
            // (rule ((= v (vs k)) (vec-get v 1)) ((set (long k) true))): vec-get has no result for a shorter vector, which
            // drops the match.
            var graph = new EGraph();
            graph.Values.RegisterVec<long>();
            var vs = graph.DefineFunction<long, Vec<long>>("vs");
            var isLong = graph.DefineFunction<long, bool>("long");
            var root = graph.Root;
            root.Assume(vs.Of(1L).Is(Vec.Of(7L)), vs.Of(2L).Is(Vec.Of(7L, 8L)));
            var rules = graph.DefineRuleset("rules");
            var rule = rules.DefineRule("long", r =>
            {
                var k = r.Var<long>("k");
                var v = r.Var<Vec<long>>("v");
                r.When(v.Is(vs.Of(k)), Fact.Exists(Primitives.VecGet<long>().Of(v, 1L)));
                r.Then(isLong.Of(k).Is(true));
            });
            Assert.Equal("(rule ((= v (vs k)) (vec-get v 1)) ((set (long k) true)) :ruleset rules)", rule.ToString());
            root.Run(rules);
            Assert.False(root.TryGet(isLong.Of(1L), out _));
            Assert.True(root.Get(isLong.Of(2L)));

            // (check (vec-get (vs 2) 1)) holds, (check (vec-get (vs 1) 1)) does not.
            Assert.True(root.Check(Fact.Exists(Primitives.VecGet<long>().Of(vs.Of(2L), 1L))));
            Assert.False(root.Check(Fact.Exists(Primitives.VecGet<long>().Of(vs.Of(1L), 1L))));
            Assert.Throws<ArgumentException>(() => Fact.Exists((Term<long>)7L));
        }

    }

}
