using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// What rule heads do, and how evaluation failures fault the scope: a primitive or lattice delegate that throws is
    /// reported with the rule and its bindings, and built-in arithmetic is partial, as egglog's is.
    /// </summary>
    public sealed class ActionTests
    {

        [Fact]
        public void GetOrAddDuplicatesAreMergedByTheRebuild()
        {
            // In parallel each rule's actions run on their own execution state, which mints its own id for a new term:
            // the two rules below mint two ids for Lit(1), and the merge unions them.
            var t = new NodeTheory(new EGraphOptions { DegreeOfParallelism = 4 });
            t.Graph.Engine.Database.ParallelRuleSetsOverride = true;
            var first = t.Graph.DefineRelation<Node>("first");
            var second = t.Graph.DefineRelation<Node>("second");
            t.Rules.DefineRule("first", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(first.Of(t.Lit.Of(x)));
            });
            t.Rules.DefineRule("second", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(second.Of(t.Lit.Of(x)));
            });
            t.Root.Assume(t.Num.Of(1L));
            var unions = t.Graph.Engine.UnionCount;

            var report = t.Root.Run(Schedule.Saturate(t.Rules));

            var lit = t.Root.Get(t.Lit.Of(1L));
            Assert.True(t.Root.Check(first.Of(lit), second.Of(lit)));
            Assert.Equal(1, t.Graph.Engine.TableSize(t.Lit.Function));
            Assert.Equal(unions + 1, t.Graph.Engine.UnionCount);
            Assert.True(report.RebuildTime > TimeSpan.Zero);
        }

        [Fact]
        public void AConstructorOutputWriteUnions()
        {
            var t = new NodeTheory();
            var link = t.Graph.DefineRelation<long, Node>("link");
            t.Rules.DefineRule("link", r =>
            {
                var (n, x) = r.Vars<long, Node>("n", "x");
                r.When(link.Of(n, x));
                r.Then(t.Lit.Of(n).Is(x));
            });
            var lit = t.Root.Make(t.Lit.Of(1L));
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(link.Of(1L, a), link.Of(2L, b));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.AreEqual(lit, a));
            Assert.True(t.Root.AreEqual(b, t.Lit.Of(2L)));
            Assert.False(t.Root.AreEqual(a, b));
        }

        [Fact]
        public void LatticeWritesMerge()
        {
            var t = new NodeTheory();
            var best = t.Graph.DefineFunction<string, long>("best", ValueLattice.Max<long>());
            var dom = t.Graph.DefineFunction<string, ValueSet<long>>("dom", ValueLattice.Intersect<long>());
            t.Rules.DefineRule("collect", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(best.Of("k").Is(x), dom.Of("k").Is(Primitives.Insert<long>().Of(ValueSet.Of(100L), x)));
            });
            t.Root.Assume(t.Num.Of(3L), t.Num.Of(9L), t.Num.Of(5L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(9L, t.Root.Get(best.Of("k")));
            Assert.Equal(ValueSet.Of(100L), t.Root.Get(dom.Of("k")));
        }

        [Fact]
        public void ARewriteWithAVariableRightSideUnions()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("double wrap", r =>
            {
                var a = r.Var<Node>("a");
                r.Rewrite(t.Wrap.Of(t.Wrap.Of(a)), a);
            });
            var inner = t.Root.Make(t.Lit.Of(1L));
            var outer = t.Root.Make(t.Wrap.Of(t.Wrap.Of(t.Lit.Of(1L))));
            Assert.False(t.Root.AreEqual(inner, outer));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.AreEqual(inner, outer));
        }

        [Fact]
        public void ARewriteWithAConstructorRightSideWritesItsOutput()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("commute", r =>
            {
                var (a, b) = r.Vars<Node, Node>("a", "b");
                r.Rewrite(t.Add.Of(a, b), t.Add.Of(b, a));
            });
            var sum = t.Root.Make(t.Add.Of(t.Lit.Of(1L), t.Lit.Of(2L)));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.AreEqual(sum, t.Add.Of(t.Lit.Of(2L), t.Lit.Of(1L))));
        }

        [Fact]
        public void MakeVariablesAreUsableInLaterHeadItems()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("make", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                var m = r.Make("m", t.Lit.Of(x));
                var w = r.Make("w", t.Wrap.Of(m));
                r.Then(t.Tag.Of(w));
            });
            t.Root.Assume(t.Num.Of(4L));

            t.Root.Run(t.Rules);

            Assert.True(t.Root.Check(t.Tag.Of(t.Wrap.Of(t.Lit.Of(4L)))));
        }

        [Fact]
        public void AConflictingRuleWriteFaultsTheScope()
        {
            var t = new NodeTheory();
            var g = t.Graph.DefineFunction<string, long>("g");
            t.Rules.DefineRule("conflict", r => r.Then(g.Of("k").Is(2L)));
            t.Root.Assume(g.Of("k").Is(1L));

            var e = Assert.Throws<RuleEvaluationException>(() => t.Root.Run(t.Rules));

            Assert.Null(e.RuleName);
            Assert.Contains("Illegal merge attempted for function g", e.Message);
            Assert.True(t.Root.IsFaulted);
            Assert.Same(e, t.Root.Failure);
            var again = Assert.Throws<InvalidOperationException>(() => t.Root.Run(t.Rules));
            Assert.Same(e, again.InnerException);
            Assert.Same(e, Assert.Throws<InvalidOperationException>(() => t.Root.Check(t.Num.Of(1L))).InnerException);
            Assert.Same(e, Assert.Throws<InvalidOperationException>(() => t.Root.Assume(t.Num.Of(1L))).InnerException);
            t.Root.Dispose();
            Assert.False(t.Root.IsDisposed);
        }

        [Fact]
        public void APrimitiveThrowingInAFilterNamesTheRuleAndItsBindings()
        {
            var t = new NodeTheory();
            var boom = new InvalidOperationException("boom");
            t.Rules.DefineRule("throwing filter", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(x, _ => throw boom);
                r.Then(t.Tag.Of(t.Lit.Of(x)));
            });
            t.Root.Assume(t.Num.Of(7L));

            var e = Assert.Throws<RuleEvaluationException>(() => t.Root.Run(t.Rules));

            Assert.Equal("throwing filter", e.RuleName);
            Assert.Equal("x=7", e.Bindings);
            Assert.Same(boom, e.InnerException);
            Assert.Contains("<lambda>", e.Message);
            Assert.True(t.Root.IsFaulted);
        }

        [Fact]
        public void APrimitiveThrowingInAHeadNamesTheRuleAndItsBindings()
        {
            var t = new NodeTheory();
            var divide = Primitive.Create<long, long, long>("divide", (a, b) => a / b);
            var zero = t.Graph.DefineRelation<long>("zero");
            t.Rules.DefineRule("throwing head", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x), zero.Of(y));
                r.Then(t.Num.Of(divide.Of(x, y)));
            });
            t.Root.Assume(t.Num.Of(8L), zero.Of(0L));

            var e = Assert.Throws<RuleEvaluationException>(() => t.Root.Run(t.Rules));

            Assert.Equal("throwing head", e.RuleName);
            Assert.Equal("x=8 y=0", e.Bindings);
            Assert.IsType<DivideByZeroException>(e.InnerException);
            Assert.Contains("divide", e.Message);
        }

        [Fact]
        public void APrimitiveThrowingInALetNamesTheRuleAndItsBindings()
        {
            var t = new NodeTheory();
            var parse = Primitive.Create<string, long>("parse", long.Parse);
            var text = t.Graph.DefineRelation<string>("text");
            t.Rules.DefineRule("parse", r =>
            {
                var (s, n) = r.Vars<string, long>("s", "n");
                r.When(text.Of(s), n.Is(parse.Of(s)));
                r.Then(t.Num.Of(n));
            });
            t.Root.Assume(text.Of("12"), text.Of("twelve"));

            var e = Assert.Throws<RuleEvaluationException>(() => t.Root.Run(t.Rules));

            Assert.Equal("parse", e.RuleName);
            Assert.Equal("s=\"twelve\"", e.Bindings);
            Assert.IsType<FormatException>(e.InnerException);
        }

        [Fact]
        public void AnOverflowOfBuiltInArithmeticInABodyDropsTheMatch()
        {
            var t = new NodeTheory();
            var below = t.Graph.DefineRelation<long>("below");
            t.Rules.DefineRule("minus one", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x), y.Is(Primitives.Subtract.Of(x, 1L)));
                r.Then(below.Of(y));
            });
            t.Root.Assume(t.Num.Of(5L), t.Num.Of(long.MinValue));

            t.Root.Run(t.Rules);

            Assert.False(t.Root.IsFaulted);
            Assert.True(t.Root.Check(below.Of(4L)));
            Assert.False(t.Root.Check(below.Of(long.MaxValue)));
        }

        [Fact]
        public void AnOverflowOfBuiltInArithmeticInAHeadFailsTheRule()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("plus one", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            t.Root.Assume(t.Num.Of(long.MaxValue));

            var e = Assert.Throws<RuleEvaluationException>(() => t.Root.Run(t.Rules));

            Assert.Equal("plus one", e.RuleName);
            Assert.StartsWith("x=9223372036854775807 ", e.Bindings);
            Assert.IsType<OverflowException>(e.InnerException);
            Assert.True(t.Root.IsFaulted);
        }

        [Fact]
        public void BindingsRenderSetsAsSets()
        {
            var t = new NodeTheory();
            var dom = t.Graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var pick = Primitive.Create<ValueSet<string>, string, bool>("pick", (_, _) => throw new InvalidOperationException("pick"));
            t.Rules.DefineRule("picks", r =>
            {
                var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
                r.When(d.Is(dom.Of(p)));
                r.Where(pick.Of(d, p));
                r.Then(t.Num.Of(1L));
            });
            t.Root.Assume(dom.Of("k").Is(ValueSet.Of("b", "a")), dom.Of("e").Is(ValueSet<string>.Empty));

            var e = Assert.Throws<RuleEvaluationException>(() => t.Root.Run(t.Rules));

            Assert.True(e.Bindings is "d={} p=\"e\"" or "d={a, b} p=\"k\"", e.Bindings);
        }

        /// <summary>
        /// <see cref="RuleEvaluationException.Bindings"/> renders a set as its documentation shows:
        /// <c>d={a, b} v="b"</c>.
        /// </summary>
        [Fact]
        public void BindingsRenderASetAsDocumented()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var pick = graph.DefineRelation<string, string>("pick");
            var rules = graph.DefineRuleset("rules");
            var boom = Primitive.Create<ValueSet<string>, string, bool>("boom", (_, _) => throw new InvalidOperationException("boom"));
            rules.DefineRule("throws on a set", r =>
            {
                var (p, d, v) = r.Vars<string, ValueSet<string>, string>("p", "d", "v");
                r.When(d.Is(dom.Of(p)), pick.Of(p, v));
                r.Where(boom.Of(d, v));
                r.Then(pick.Of(v, p));
            });
            graph.Root.Assume(dom.Of("k").Is(ValueSet.Of("a", "b")), pick.Of("k", "b"));

            var e = Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(rules));
            Assert.Equal("d={a, b} v=\"b\"", e.Bindings);
        }

        /// <summary>
        /// egglog's <c>+</c> is partial (<c>checked_add</c>): in a body an overflow drops the match, and only a head
        /// call fails. <see cref="Primitives.Add"/> is partial the same way, so the run completes and the root is not
        /// faulted.
        /// </summary>
        [Fact]
        public void ABodyOverflowDropsTheMatchAsInEgglog()
        {
            var t = new NodeTheory();
            var hit = t.Graph.DefineRelation<long>("hit");
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(long.MaxValue));
            t.Rules.DefineRule("plus one", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x), y.Is(Primitives.Add.Of(x, 1L)));
                r.Then(hit.Of(y));
            });
            var thrown = Record.Exception(() => t.Root.Run(Schedule.Saturate(t.Rules)));
            Assert.Null(thrown);
            Assert.False(t.Root.IsFaulted);
        }

    }

}
