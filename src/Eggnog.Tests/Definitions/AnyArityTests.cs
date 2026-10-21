using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Definitions
{

    /// <summary>
    /// Tables and primitives of any arity, as egglog's take, where the typed API stops at four: relations, functions and
    /// constructors over five and more columns, with typed outputs, in host writes and reads, rules and extraction; and
    /// primitives over five and more parameters of their own types, total and partial.
    /// </summary>
    public sealed class AnyArityTests
    {

        static readonly Type[] Five = { typeof(long), typeof(long), typeof(long), typeof(long), typeof(string) };

        [Fact]
        public void ARelationOfFiveColumnsHoldsAndJoinsItsTuples()
        {
            var t = new NodeTheory();
            var r = t.Graph.DefineRelationN("r", Five);
            var seen = t.Graph.DefineRelation<string>("seen");
            t.Rules.DefineRule("join", rule =>
            {
                var (a, s) = rule.Vars<long, string>("a", "s");
                rule.When(r.Of(a, 2L, 3L, 4L, s), t.Num.Of(a));
                rule.Then(seen.Of(s));
            });
            t.Root.Assume(r.Of(1L, 2L, 3L, 4L, "x"), t.Num.Of(1L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.Check(r.Of(1L, 2L, 3L, 4L, "x")));
            Assert.True(t.Root.Check(seen.Of("x")));
            Assert.Equal(5, r.KeyArray.Length);
        }

        [Fact]
        public void AFunctionOfFiveKeysHasATypedValueAndMerges()
        {
            var graph = new EGraph();
            var best = graph.DefineFunctionN<long>("best", Five, ValueLattice.Max<long>());
            var sum = graph.DefineFunctionN<long>("sum", Five, FunctionMerge.Of<long>("sum", (a, b) => a + b));
            object[] key = { 1L, 2L, 3L, 4L, "k" };

            graph.Root.Assume(best.Of(key).Is(3L), best.Of(key).Is(7L), sum.Of(key).Is(3L), sum.Of(key).Is(7L));

            long value = graph.Root.Get(best.Of(key));
            Assert.Equal(7L, value);
            Assert.Equal(10L, graph.Root.Get(sum.Of(key)));
        }

        /// <summary>
        /// A function of five keys outputs a class, as egglog's functions of a sort do; the any-arity views read a scope's
        /// rows, and refuse a table with a sort column, as the typed views do.
        /// </summary>
        [Fact]
        public void AFunctionOfFiveKeysOutputsAClassAndTheViewsReadAScope()
        {
            var t = new NodeTheory();
            var r = t.Graph.DefineRelationN("r", Five);
            var f = t.Graph.DefineFunctionN<Node>("f", Five);
            var n = t.Graph.DefineFunctionN<long>("n", Five);
            var seen = t.Graph.DefineQuery("seen", q => q.When(r.In(t.Graph.RootTag).Of(q.Var<long>("x"), 2L, 3L, 4L, "x")));
            var valued = t.Graph.DefineQuery("valued", q => q.When(n.In(t.Graph.RootTag).Of(1L, 2L, 3L, 4L, "x").Is(q.Var<long>("v"))));
            var a = t.Root.Make(t.A.Of());

            t.Root.Assume(r.Of(1L, 2L, 3L, 4L, "x"), r.Of(5L, 2L, 3L, 4L, "x"), f.Of(1L, 2L, 3L, 4L, "x").Is(a), n.Of(1L, 2L, 3L, 4L, "x").Is(6L));

            Assert.True(t.Root.AreEqual(t.Root.Get(f.Of(1L, 2L, 3L, 4L, "x")), a));
            Assert.Equal(2, t.Root.Count(seen));
            Assert.Equal(1, t.Root.Count(valued));
            Assert.Throws<ArgumentException>(() => f.In(t.Graph.RootTag));
        }

        /// <summary>
        /// A constructor of five children makes terms, matches in rules and extracts as egglog renders them.
        /// </summary>
        [Fact]
        public void AConstructorOfFiveChildrenMakesAndExtractsTerms()
        {
            var t = new NodeTheory();
            var big = t.Graph.DefineConstructorN<Node>("Big", Enumerable.Repeat(typeof(Node), 5).ToArray());
            var a = t.Root.Make(t.A.Of());

            var term = t.Root.Make(big.Of(a, a, a, a, t.B.Of()));

            Assert.True(t.Root.AreEqual(term, big.Of(t.A.Of(), t.A.Of(), t.A.Of(), t.A.Of(), t.B.Of())));
            Assert.Equal("(Big (A) (A) (A) (A) (B))", t.Root.Extract(term).ToString());
        }

        /// <summary>
        /// A primitive of five parameters of their own types computes in a host write and a rule's head; a partial one
        /// with no result drops a body's match.
        /// </summary>
        [Fact]
        public void APrimitiveOfFiveParametersComputesAndAPartialOneDropsTheMatch()
        {
            var t = new NodeTheory();
            var join = Primitive.Create<string>("join5", new[] { typeof(long), typeof(string), typeof(bool), typeof(double), typeof(long) },
                args => FormattableString.Invariant($"{args[0]}{args[1]}{args[2]}{args[3]}{args[4]}"));
            var positive = Primitive.CreatePartial<long>("positive5", Five, (System.Collections.Generic.IReadOnlyList<object> args, out long result) =>
            {
                result = (long)args[0];
                return result > 0;
            });
            var text = t.Graph.DefineFunction<string, string>("text");
            var kept = t.Graph.DefineRelation<long>("kept");
            t.Rules.DefineRule("keep", rule =>
            {
                var (x, y) = rule.Vars<long, long>("x", "y");
                rule.When(t.Num.Of(x), y.Is(positive.Of(x, 0L, 0L, 0L, "s")));
                rule.Then(kept.Of(y), text.Of("rule").Is(join.Of(x, "-", true, 1.5, x)));
            });
            t.Root.Assume(t.Num.Of(-1L), t.Num.Of(2L), text.Of("host").Is(join.Of(1L, "a", false, 0.5, 9L)));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.Equal("1aFalse0.59", t.Root.Get(text.Of("host")));
            Assert.Equal("2-True1.52", t.Root.Get(text.Of("rule")));
            Assert.Equal(1, t.Root.CountRows(kept));
            Assert.True(positive.IsPartial);
        }

        /// <summary>
        /// A primitive of five parameters takes and returns classes, as egglog's take any sort, in a rule's head and a host
        /// write, and computes in an expression merge.
        /// </summary>
        [Fact]
        public void APrimitiveOfFiveParametersTakesClassesAndMerges()
        {
            var t = new NodeTheory();
            var choose = Primitive.Create<Node>("choose5", new[] { typeof(Node), typeof(Node), typeof(long), typeof(long), typeof(long) },
                args => (long)args[2] > 0 ? (Node)args[0] : (Node)args[1]);
            var sum = Primitive.Create<long>("sum5", new[] { typeof(long), typeof(long), typeof(long), typeof(long), typeof(long) },
                args => (long)args[0] + (long)args[1] + (long)args[2] + (long)args[3] + (long)args[4]);
            var pick = t.Graph.DefineFunction<string, Node>("pick");
            var total = t.Graph.DefineFunction<string, long>("total", FunctionMerge.Expression<long>((old, @new) => sum.Of(old, @new, 1L, 0L, 0L)));
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Rules.DefineRule("choose", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(t.Num.Of(x));
                rule.Then(pick.Of("rule").Is(choose.Of(a, b, x, 0L, 0L)));
            });
            t.Root.Assume(t.Num.Of(-1L), pick.Of("host").Is(choose.Of(a, b, 1L, 0L, 0L)), total.Of("k").Is(3L), total.Of("k").Is(4L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.AreEqual(t.Root.Get(pick.Of("rule")), b));
            Assert.True(t.Root.AreEqual(t.Root.Get(pick.Of("host")), a));
            Assert.Equal(8L, t.Root.Get(total.Of("k")));
        }

        /// <summary>
        /// A partial primitive of five parameters with no result fails a rule's head, as a typed one does, where egglog
        /// panics.
        /// </summary>
        [Fact]
        public void APartialPrimitiveOfFiveParametersWithNoResultFailsAHead()
        {
            var t = new NodeTheory();
            var positive = Primitive.CreatePartial<long>("positive5", Five, (System.Collections.Generic.IReadOnlyList<object> args, out long result) =>
            {
                result = (long)args[0];
                return result > 0;
            });
            var kept = t.Graph.DefineRelation<long>("kept");
            t.Rules.DefineRule("keep", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(t.Num.Of(x));
                rule.Then(kept.Of(positive.Of(x, 0L, 0L, 0L, "s")));
            });
            t.Root.Assume(t.Num.Of(-1L));

            Assert.Throws<RuleEvaluationException>(() => t.Root.Run(t.Rules));
        }

        /// <summary>
        /// A class given as a value to an any-arity application is a class constant, with a typed one's checks: a global
        /// rule may hold classes only from the root.
        /// </summary>
        [Fact]
        public void AClassGivenAsAValueIsCheckedAsAClassConstant()
        {
            var t = new NodeTheory();
            var wide = t.Graph.DefineRelationN("wide", new[] { typeof(Node), typeof(long), typeof(long), typeof(long), typeof(long) });
            var child = t.Root.CreateChild();
            var local = child.Make(t.A.Of());

            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("leak", rule => rule.When(wide.Of(local, 1L, 2L, 3L, 4L))));

            Assert.Contains("a global rule may hold only classes from the root", e.Message, StringComparison.Ordinal);
            GC.KeepAlive(child);
        }

        [Fact]
        public void AnApplicationOfTheWrongArityOrTypesIsRefused()
        {
            var graph = new EGraph();
            var r = graph.DefineRelationN("r", Five);
            var p = Primitive.Create<long>("p5", Five, args => 0L);

            Assert.Throws<ArgumentException>(() => r.Of(1L));
            Assert.Throws<ArgumentException>(() => r.Of(1L, 2L, 3L, 4L, 5L));
            Assert.Throws<ArgumentException>(() => r.Of(1, 2L, 3L, 4L, "s"));
            Assert.Throws<ArgumentException>(() => r.Of(1L, 2L, 3L, 4L, (Term<long>)5L));
            Assert.Throws<ArgumentNullException>(() => r.Of(1L, 2L, null!, 4L, "s"));
            Assert.Throws<ArgumentException>(() => p.Of("x", 2L, 3L, 4L, "s"));
            Assert.Throws<ArgumentException>(() => graph.DefineRelationN("bad", new[] { typeof(Uri) }));
        }

    }

}
