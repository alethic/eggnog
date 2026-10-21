using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Merges that are expressions over the old and the new value (<see cref="FunctionMerge.Expression{T}"/>), egglog's
    /// <c>:merge</c> of any expression: primitives and constants over values, and calls of constructors and functions,
    /// which the merge makes or looks up; what fails such a merge; what a batch does with one; and the refusals.
    /// </summary>
    public sealed class FunctionMergeExpressionTests
    {

        [Fact]
        public void AnExpressionOfPrimitivesAndConstantsMergesValues()
        {
            var graph = new EGraph();
            var score = graph.DefineFunction<string, long>("score", FunctionMerge.Expression<long>((old, @new) => Primitives.Max.Of(old, Primitives.Add.Of(@new, 1L))));
            graph.Root.Assume(score.Of("k").Is(5L));

            graph.Root.Assume(score.Of("k").Is(2L));
            Assert.Equal(5L, graph.Root.Get(score.Of("k")));
            graph.Root.Assume(score.Of("k").Is(9L));
            Assert.Equal(10L, graph.Root.Get(score.Of("k")));
            Assert.Equal("(max old (+ new 1))", ((FunctionHandle)score).Merge!.Name);
        }

        /// <summary>
        /// A merge of classes may call a constructor, which makes the term: <c>:merge (Add old new)</c>.
        /// </summary>
        [Fact]
        public void AnExpressionOverClassesMakesTerms()
        {
            var t = new NodeTheory();
            var joined = t.Graph.DefineFunction<string, Node>("joined", FunctionMerge.Expression<Node>((old, @new) => t.Add.Of(old, @new)));
            t.Root.Assume(joined.Of("k").Is(t.A.Of()));

            t.Root.Assume(joined.Of("k").Is(t.B.Of()));

            Assert.True(t.Root.AreEqual(t.Root.Get(joined.Of("k")), t.Add.Of(t.A.Of(), t.B.Of())));
            Assert.Equal("(Add old new)", ((FunctionHandle)joined).Merge!.Name);
        }

        /// <summary>
        /// A merge may look a function up; a rule's writes fold through it as any merge's do.
        /// </summary>
        [Fact]
        public void AnExpressionLooksAFunctionUp()
        {
            var t = new NodeTheory();
            var weight = t.Graph.DefineFunction<long, long>("weight");
            var total = t.Graph.DefineFunction<string, long>("total", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, weight.Of(@new))));
            t.Rules.DefineRule("add", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(total.Of("k").Is(x));
            });
            t.Root.Assume(weight.Of(2L).Is(20L), weight.Of(3L).Is(30L), total.Of("k").Is(1L), t.Num.Of(2L), t.Num.Of(3L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(51L, t.Root.Get(total.Of("k")));
        }

        /// <summary>
        /// A lookup with no row fails the merge, as egglog's merge panics, naming the lookup: a rule's run fails and the
        /// scope is faulted. So does a primitive that throws in a rule's merge; in a host write, an expression without
        /// tables is evaluated as the batch is planned, so the same failure writes nothing and faults nothing.
        /// </summary>
        [Fact]
        public void AFailedLookupOrPrimitiveFailsTheMerge()
        {
            var t = new NodeTheory();
            var weight = t.Graph.DefineFunction<long, long>("weight");
            var total = t.Graph.DefineFunction<string, long>("total", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, weight.Of(@new))));
            t.Rules.DefineRule("add four", r => r.Then(total.Of("k").Is(4L)));
            t.Root.Assume(total.Of("k").Is(1L));

            var lookup = Assert.Throws<RuleEvaluationException>(() => t.Root.Run(t.Rules));
            Assert.Contains("Lookup on weight failed in the merge function for total", lookup.ToString(), StringComparison.Ordinal);
            Assert.True(t.Root.IsFaulted);

            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, @new)));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("one", r => r.Then(sum.Of("k").Is(1L)));
            graph.Root.Assume(sum.Of("k").Is(long.MaxValue));

            var host = Assert.Throws<RuleEvaluationException>(() => graph.Root.Assume(sum.Of("k").Is(1L)));
            Assert.False(graph.Root.IsFaulted);
            Assert.Equal(long.MaxValue, graph.Root.Get(sum.Of("k")));
            var rule = Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(rules));
            Assert.Contains("merge (+ old new) of function 'sum'", rule.Message, StringComparison.Ordinal);
            Assert.True(graph.Root.IsFaulted);
            Assert.NotNull(host.InnerException);
        }

        /// <summary>
        /// A batch works out an expression of primitives itself, so two writes in one batch fold and a later read sees the
        /// merged value; an expression that calls tables only the engine evaluates, so the batch stages each write as it
        /// comes and refuses a later read of the key, which a later call can make.
        /// </summary>
        [Fact]
        public void ABatchSimulatesAnExpressionOfPrimitivesAndStagesOneThatCallsTables()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, @new)));
            var weight = graph.DefineFunction<long, long>("weight");
            var total = graph.DefineFunction<string, long>("total", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, weight.Of(@new))));
            var copy = graph.DefineFunction<string, long>("copy");

            graph.Root.Assume(sum.Of("k").Is(2L), sum.Of("k").Is(2L), copy.Of("sum").Is(sum.Of("k")));
            Assert.Equal(4L, graph.Root.Get(sum.Of("k")));
            Assert.Equal(4L, graph.Root.Get(copy.Of("sum")));

            graph.Root.Assume(weight.Of(1L).Is(10L), total.Of("k").Is(0L));
            graph.Root.Assume(total.Of("k").Is(1L), total.Of("k").Is(1L));
            Assert.Equal(20L, graph.Root.Get(total.Of("k")));
            var e = Assert.Throws<InvalidOperationException>(() => graph.Root.Assume(total.Of("k").Is(1L), copy.Of("total").Is(total.Of("k"))));
            Assert.Contains("read it in a later call", e.Message, StringComparison.Ordinal);
            Assert.Equal(20L, graph.Root.Get(total.Of("k")));
        }

        /// <summary>
        /// The flush merges a table an expression reads before the expression, so a later write of that table in the
        /// batch would reach the earlier write's merge, which egglog's commands, one after the other, do not let it; and
        /// a merge of classes earlier in the batch would not have reached the expression's classes yet. Both orders are
        /// refused; separate calls give egglog's result.
        /// </summary>
        [Fact]
        public void ABatchRefusesWhatTheFlushWouldReorderForAnExpression()
        {
            var t = new NodeTheory();
            var weight = t.Graph.DefineFunction<long, long>("weight", ValueLattice.Max<long>());
            var total = t.Graph.DefineFunction<string, long>("total", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, weight.Of(@new))));
            var joined = t.Graph.DefineFunction<string, Node>("joined", FunctionMerge.Expression<Node>((old, @new) => t.Add.Of(old, @new)));
            t.Root.Assume(weight.Of(4L).Is(10L), total.Of("k").Is(1L), joined.Of("k").Is(t.A.Of()));

            var later = Assert.Throws<InvalidOperationException>(() => t.Root.Assume(total.Of("k").Is(4L), weight.Of(4L).Is(40L)));
            var merged = Assert.Throws<InvalidOperationException>(() => t.Root.Assume(t.A.Of().Is(t.B.Of()), joined.Of("k").Is(t.B.Of())));

            Assert.Contains("whose merge (+ old (weight new)) reads it", later.Message, StringComparison.Ordinal);
            Assert.Contains("after this batch merged classes", merged.Message, StringComparison.Ordinal);
            t.Root.Assume(total.Of("k").Is(4L));
            t.Root.Assume(weight.Of(4L).Is(40L));
            Assert.Equal(11L, t.Root.Get(total.Of("k")));
        }

        /// <summary>
        /// A union that makes two keys equal merges their classes with the expression in the rebuild, the kept row's as
        /// the old; and a merge of a class with itself makes nothing, as egglog's function merge returns early.
        /// </summary>
        [Fact]
        public void TheRebuildMergesWithTheExpressionAndAnEqualClassMakesNothing()
        {
            var t = new NodeTheory();
            var joined = t.Graph.DefineFunction<Node, Node>("joined", FunctionMerge.Expression<Node>((old, @new) => t.Add.Of(old, @new)));
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            t.Root.Assume(joined.Of(a).Is(one), joined.Of(b).Is(two));

            t.Root.Assume(joined.Of(a).Is(one));
            Assert.Equal(0, t.Root.CountRows(t.Add));

            t.Root.Union(b, a);
            Assert.Equal(1, t.Root.CountRows(joined));
            Assert.True(t.Root.AreEqual(t.Root.Get(joined.Of(a)), t.Add.Of(t.Lit.Of(1L), t.Lit.Of(2L))));
        }

        /// <summary>
        /// Only the root writes a function with an expression: a child's write is refused, and so is its union of a class
        /// that keys one of the function's rows.
        /// </summary>
        [Fact]
        public void AChildCannotWriteAFunctionWithAnExpression()
        {
            var t = new NodeTheory();
            var sum = t.Graph.DefineFunction<string, long>("sum", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, @new)));
            var joined = t.Graph.DefineFunction<Node, Node>("joined", FunctionMerge.Expression<Node>((old, @new) => t.Add.Of(old, @new)));
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(joined.Of(b).Is(a));
            var child = t.Root.CreateChild();

            Assert.Throws<InvalidOperationException>(() => child.Assume(sum.Of("k").Is(1L)));
            Assert.Throws<InvalidOperationException>(() => child.Union(a, b));
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// The expression is checked when its function is defined: it may hold only its own old and new values, no class
        /// constant, no primitive over a sort, no table of another graph and no scoped view; and it is a term, not
        /// <c>null</c>. A refused definition leaves no table behind.
        /// </summary>
        [Fact]
        public void AnExpressionIsCheckedWhenItsFunctionIsDefined()
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            var values = t.Graph.DefineFunction<long, long>("values");
            Var<long> leaked = null!;
            t.Graph.DefineQuery("leak", q =>
            {
                leaked = q.Var<long>("v");
                q.When(t.Num.Of(leaked));
            });
            var a = t.Root.Make(t.A.Of());
            var tables = t.Graph.Tables.Count;

            var foreign = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, long>("f", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, leaked))));
            var constant = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, Node>("g", FunctionMerge.Expression<Node>((old, @new) => t.Add.Of(old, a))));
            var sort = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, Node>("h", FunctionMerge.Expression<Node>((old, @new) => Primitives.OrderingMin<Node>().Of(old, @new))));
            var graph = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, Node>("i", FunctionMerge.Expression<Node>((old, @new) => other.Add.Of(old, @new))));
            var scoped = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, long>("j", FunctionMerge.Expression<long>((old, @new) => values.In(t.Graph.RootTag).Of(@new))));
            Assert.Throws<ArgumentException>(() => FunctionMerge.Expression<long>((old, @new) => null!));

            Assert.Contains("is not its old or new value", foreign.Message, StringComparison.Ordinal);
            Assert.Contains("apply a nullary constructor or function instead", constant.Message, StringComparison.Ordinal);
            Assert.Contains("over a sort", sort.Message, StringComparison.Ordinal);
            Assert.Contains("a table of another graph", graph.Message, StringComparison.Ordinal);
            Assert.Contains("a merge reads its own scope", scoped.Message, StringComparison.Ordinal);
            Assert.Equal(tables, t.Graph.Tables.Count);
        }

    }

}
