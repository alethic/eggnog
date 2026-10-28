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
            Assert.False(t.Root.IsFaulted);

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
            Assert.False(graph.Root.IsFaulted);
            Assert.Equal(long.MaxValue, graph.Root.Get(sum.Of("k")));
            Assert.NotNull(host.InnerException);
        }

        /// <summary>
        /// A batch works out an expression of primitives itself, so two writes in one batch fold and a later read sees the
        /// merged value; an expression that calls tables only the engine evaluates, so a later read of the key sees it after
        /// the batch flushes, as egglog's next command does: (set (total "k") 1) (set (copy "total") (total "k")) gives 30.
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
            graph.Root.Assume(total.Of("k").Is(1L), copy.Of("total").Is(total.Of("k")));
            Assert.Equal(30L, graph.Root.Get(total.Of("k")));
            Assert.Equal(30L, graph.Root.Get(copy.Of("total")));
        }

        /// <summary>
        /// The flush merges a table an expression reads before the expression, so a later write of that table in the
        /// batch, or a merge of classes before the expression's write, comes after the batch flushes what it planned, as
        /// egglog's commands, one after the other, run: (set (total "k") 4) (set (weight 4) 40) leaves 11, and (union (A)
        /// (B)) (set (joined "k") (B)) leaves (A), which the new value equals, so the merge does not run.
        /// </summary>
        [Fact]
        public void ABatchKeepsEgglogsOrderForAnExpression()
        {
            var t = new NodeTheory();
            var weight = t.Graph.DefineFunction<long, long>("weight", ValueLattice.Max<long>());
            var total = t.Graph.DefineFunction<string, long>("total", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, weight.Of(@new))));
            var joined = t.Graph.DefineFunction<string, Node>("joined", FunctionMerge.Expression<Node>((old, @new) => t.Add.Of(old, @new)));
            t.Root.Assume(weight.Of(4L).Is(10L), total.Of("k").Is(1L), joined.Of("k").Is(t.A.Of()));

            t.Root.Assume(total.Of("k").Is(4L), weight.Of(4L).Is(40L));
            t.Root.Assume(t.A.Of().Is(t.B.Of()), joined.Of("k").Is(t.B.Of()));

            Assert.Equal(11L, t.Root.Get(total.Of("k")));
            Assert.Equal(40L, t.Root.Get(weight.Of(4L)));
            Assert.True(t.Root.AreEqual(t.Root.Get(joined.Of("k")), t.A.Of()));
            Assert.Equal(0L, t.Root.CountRows(t.Add));
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
        /// A class constant of the root is the class it names as it is canonical when the merge runs, as egglog's read of
        /// a global is: (let $g (A)) (function h () E :merge $g) (set (h) (B)) (set (h) (A)) gives (A); a value equal to
        /// the old one leaves the row, as egglog's, so (set (k) (B)) (set (k) (B)) gives (B); and after (union $g (C))
        /// the merge gives the class of both.
        /// </summary>
        [Fact]
        public void AClassConstantIsItsClassAsItIsCanonicalWhenTheMergeRuns()
        {
            var t = new NodeTheory();
            var winner = t.Root.Make(t.Lit.Of(2L));
            var g = t.Root.Make(t.Lit.Of(1L));
            var h = t.Graph.DefineFunction<string, Node>("h", FunctionMerge.Expression<Node>((old, @new) => g));
            var k = t.Graph.DefineFunction<string, Node>("k", FunctionMerge.Expression<Node>((old, @new) => g));
            var m = t.Graph.DefineFunction<string, Node>("m", FunctionMerge.Expression<Node>((old, @new) => g));

            t.Root.Assume(h.Of("x").Is(t.B.Of()));
            t.Root.Assume(h.Of("x").Is(t.A.Of()));
            t.Root.Assume(k.Of("x").Is(t.B.Of()));
            t.Root.Assume(k.Of("x").Is(t.B.Of()));
            Assert.True(t.Root.AreEqual(t.Root.Get(h.Of("x")), g));
            Assert.True(t.Root.AreEqual(t.Root.Get(k.Of("x")), t.B.Of()));
            Assert.False(t.Root.AreEqual(t.Root.Get(k.Of("x")), g));

            t.Root.Union(g, winner);
            t.Root.Assume(m.Of("x").Is(t.A.Of()));
            t.Root.Assume(m.Of("x").Is(t.B.Of()));
            Assert.True(t.Root.AreEqual(t.Root.Get(m.Of("x")), winner));
            Assert.Equal(t.Root.Find(winner), t.Root.Get(m.Of("x")));
        }

        /// <summary>
        /// A batch that writes a class constant's function and then reads the merged value reads the class as it is
        /// canonical: after (union $g (Lit 2)) has made (Lit 2) the class's id, (set (h) (A)) and then (Wrap (h)) find
        /// the term (Wrap (Lit 2)) and its class, and (set (f (h)) 5) and (set (f (Lit 2)) 6) write one key of f, which
        /// has no merge, so the batch refuses the second and keeps the first.
        /// </summary>
        [Fact]
        public void ABatchReadsTheMergedClassConstantCanonical()
        {
            var t = new NodeTheory();
            var winner = t.Root.Make(t.Lit.Of(2L));
            var g = t.Root.Make(t.Lit.Of(1L));
            var wrapped = t.Root.Make(t.Wrap.Of(winner));
            var h = t.Graph.DefineFunction<string, Node>("h", FunctionMerge.Expression<Node>((old, @new) => g));
            t.Root.Union(g, winner);
            t.Root.Assume(h.Of("x").Is(t.B.Of()));

            var made = default(Node);
            t.Root.Write(w =>
            {
                w.Assume(h.Of("x").Is(t.A.Of()));
                made = w.Make(t.Wrap.Of(h.Of("x")));
            });

            Assert.Equal(wrapped, made);

            var f = t.Graph.DefineFunction<Node, long>("f");
            Assert.Throws<FactConflictException>(() => t.Root.Assume(h.Of("x").Is(t.A.Of()), f.Of(h.Of("x")).Is(5L), f.Of(winner).Is(6L)));
            Assert.Equal(5L, t.Root.Get(f.Of(winner)));
            Assert.Equal(1, t.Root.CountRows(f));
        }

        /// <summary>
        /// Under a primitive, in a function of another sort, a class constant is its class also when the old and new
        /// values agree: :merge (vec-push old $g) of (vec-of (B)) and (vec-of (B)) gives (vec-of (B) (A)), by a host's
        /// write, which a later fact of its batch reads, and by a rule's. egglog's read of the global, a function node,
        /// yields the old value then (lib.rs:1222-1226), so it pushes the vector itself as an E and leaves (h)
        /// unextractable.
        /// </summary>
        [Fact]
        public void UnderAPrimitiveAClassConstantIsItsClassWhenTheValuesAgree()
        {
            var t = new NodeTheory();
            t.Graph.Values.RegisterVec<Node>();
            var of = Primitives.VecOf<Node>();
            var g = t.Root.Make(t.A.Of());
            var h = t.Graph.DefineFunction<string, Vec<Node>>("h", FunctionMerge.Expression<Vec<Node>>((old, @new) => Primitives.VecPush<Node>().Of(old, g)));
            var k = t.Graph.DefineFunction<long, Vec<Node>>("k", FunctionMerge.Expression<Vec<Node>>((old, @new) => Primitives.VecPush<Node>().Of(old, g)));
            var n = t.Graph.DefineFunction<Vec<Node>, long>("n");
            t.Rules.DefineRule("set", r =>
            {
                r.Then(k.Of(1L).Is(of.Of(t.B.Of())));
                r.Then(k.Of(1L).Is(of.Of(t.B.Of())));
            });

            t.Root.Assume(h.Of("x").Is(of.Of(t.B.Of())));
            t.Root.Assume(h.Of("x").Is(of.Of(t.B.Of())), n.Of(h.Of("x")).Is(7L));
            t.Root.Run(t.Rules);

            Assert.True(t.Root.Check(h.Of("x").Is(of.Of(t.B.Of(), t.A.Of()))));
            Assert.Equal(7L, t.Root.Get(n.Of(of.Of(t.B.Of(), t.A.Of()))));
            Assert.True(t.Root.Check(k.Of(1L).Is(of.Of(t.B.Of(), t.A.Of()))));
        }

        /// <summary>
        /// Under a primitive, in a function of the class's sort, a class constant keeps the old value when the old and new
        /// values agree, as egglog's read of the global, a function node, does at any depth (lib.rs:1222-1226):
        /// (let $c (Lit 1)) (function f () E :merge (vec-get (vec-of $c) 0)) (set (f) (B)) (set (f) (B)) gives (B), by a
        /// host's write and by a rule's, and (set (f) (B)) (set (f) (A)) gives (Lit 1).
        /// </summary>
        [Fact]
        public void UnderAPrimitiveAClassConstantKeepsTheOldValueOfItsSortWhenTheValuesAgree()
        {
            var t = new NodeTheory();
            t.Graph.Values.RegisterVec<Node>();
            var g = t.Root.Make(t.Lit.Of(1L));
            var f = t.Graph.DefineFunction<string, Node>("f", FunctionMerge.Expression<Node>((old, @new) => Primitives.VecGet<Node>().Of(Primitives.VecOf<Node>().Of(g), 0L)));
            var k = t.Graph.DefineFunction<long, Node>("k", FunctionMerge.Expression<Node>((old, @new) => Primitives.VecGet<Node>().Of(Primitives.VecOf<Node>().Of(g), 0L)));
            t.Rules.DefineRule("set", r =>
            {
                r.Then(k.Of(1L).Is(t.B.Of()));
                r.Then(k.Of(1L).Is(t.B.Of()));
            });

            t.Root.Assume(f.Of("x").Is(t.B.Of()));
            t.Root.Assume(f.Of("x").Is(t.B.Of()));
            t.Root.Assume(f.Of("y").Is(t.B.Of()));
            t.Root.Assume(f.Of("y").Is(t.A.Of()));
            t.Root.Run(t.Rules);

            Assert.True(t.Root.AreEqual(t.Root.Get(f.Of("x")), t.B.Of()));
            Assert.True(t.Root.AreEqual(t.Root.Get(f.Of("y")), g));
            Assert.True(t.Root.AreEqual(t.Root.Get(k.Of(1L)), t.B.Of()));
        }

        /// <summary>
        /// A batch that merged classes flushes and rebuilds before a write of a function whose merge holds class
        /// constants, whatever its output, as egglog's next command reads the global after the rebuild: with
        /// :merge (+ old (+ -2 (set-length (set-of $g $g2)))) and (h) at 10, (union $g $g2) (set (h) 5) gives 9.
        /// </summary>
        [Fact]
        public void ABatchMergesThroughClassConstantsAfterItsUnion()
        {
            var t = new NodeTheory();
            t.Graph.Values.RegisterSet<Node>();
            var g = t.Root.Make(t.A.Of());
            var g2 = t.Root.Make(t.B.Of());
            var h = t.Graph.DefineFunction<string, long>("h", FunctionMerge.Expression<long>((old, @new) =>
                Primitives.Add.Of(old, Primitives.Add.Of(-2L, Primitives.Count<Node>().Of(Primitives.SetOf<Node>().Of(g, g2))))));
            t.Root.Assume(h.Of("x").Is(10L));

            t.Root.Write(w =>
            {
                w.Union(w.Make(t.A.Of()), w.Make(t.B.Of()));
                w.Assume(h.Of("x").Is(5L));
            });

            Assert.Equal(9L, t.Root.Get(h.Of("x")));
        }

        /// <summary>
        /// A later fact of the batch reads the value such a merge leaves after the union: with
        /// :merge (+ old (set-length (set-of $g $g2))) and (h) at 10, (union $g $g2) (set (h) 5) (set (n (h)) 1) gives
        /// (h) 11 and (n 11) 1.
        /// </summary>
        [Fact]
        public void ABatchReadsWhatAMergeThroughClassConstantsLeavesAfterItsUnion()
        {
            var t = new NodeTheory();
            t.Graph.Values.RegisterSet<Node>();
            var g = t.Root.Make(t.A.Of());
            var g2 = t.Root.Make(t.B.Of());
            var h = t.Graph.DefineFunction<string, long>("h", FunctionMerge.Expression<long>((old, @new) =>
                Primitives.Add.Of(old, Primitives.Count<Node>().Of(Primitives.SetOf<Node>().Of(g, g2)))));
            var n = t.Graph.DefineFunction<long, long>("n");
            t.Root.Assume(h.Of("x").Is(10L));

            t.Root.Write(w =>
            {
                w.Union(w.Make(t.A.Of()), w.Make(t.B.Of()));
                w.Assume(h.Of("x").Is(5L));
                w.Assume(n.Of(h.Of("x")).Is(1L));
            });

            Assert.Equal(11L, t.Root.Get(h.Of("x")));
            Assert.True(t.Root.Check(n.Of(11L).Is(1L)));
            Assert.Equal(1, t.Root.CountRows(n));
        }

        /// <summary>
        /// A write through a class constant before the batch's union reads the classes as the last rebuild left them, as
        /// egglog's (set (h) 5) merges before the (union $g $g2) command after it: with
        /// :merge (+ old (set-length (set-of $g $g2))) and (h) at 10, (set (h) 5) (union $g $g2) (set (n (h)) 1) gives (h)
        /// 12 and (n 12) 1, by one batch as by one fact list.
        /// </summary>
        [Fact]
        public void ABatchMergesThroughClassConstantsBeforeALaterUnion()
        {
            var t = new NodeTheory();
            t.Graph.Values.RegisterSet<Node>();
            var g = t.Root.Make(t.A.Of());
            var g2 = t.Root.Make(t.B.Of());
            var h = t.Graph.DefineFunction<string, long>("h", FunctionMerge.Expression<long>((old, @new) =>
                Primitives.Add.Of(old, Primitives.Count<Node>().Of(Primitives.SetOf<Node>().Of(g, g2)))));
            var n = t.Graph.DefineFunction<long, long>("n");
            t.Root.Assume(h.Of("x").Is(10L));

            t.Root.Write(w =>
            {
                w.Assume(h.Of("x").Is(5L));
                w.Union(w.Make(t.A.Of()), w.Make(t.B.Of()));
                w.Assume(n.Of(h.Of("x")).Is(1L));
            });

            Assert.Equal(12L, t.Root.Get(h.Of("x")));
            Assert.True(t.Root.Check(n.Of(12L).Is(1L)));
            Assert.Equal(1, t.Root.CountRows(n));

            var u = t.Root.Make(t.Lit.Of(1L));
            var v = t.Root.Make(t.Lit.Of(2L));
            var k = t.Graph.DefineFunction<string, long>("k", FunctionMerge.Expression<long>((old, @new) =>
                Primitives.Add.Of(old, Primitives.Count<Node>().Of(Primitives.SetOf<Node>().Of(u, v)))));
            t.Root.Assume(k.Of("x").Is(10L));
            t.Root.Assume(k.Of("x").Is(5L), t.Lit.Of(1L).Is(t.Lit.Of(2L)));
            Assert.Equal(12L, t.Root.Get(k.Of("x")));
        }

        /// <summary>
        /// A rule's write through a class constant reads the class as the last rebuild left it, not as the run's own union
        /// leaves it, as egglog's merge reads the row of its lowered global: (let $g2 (B)) (let $g (A)) with (cnt $g) 1,
        /// (cnt $g2) 2, :merge (+ old (cnt $g)) and (h) at 10, (rule () ((union $g $g2) (set (h) 5))) gives 11, reading
        /// (cnt (A)); a later (set (h) 5), after the rebuild, reads the class of both and gives 13.
        /// </summary>
        [Fact]
        public void ARuleMergesThroughAClassConstantAsTheLastRebuildLeftIt()
        {
            var t = new NodeTheory();
            var g2 = t.Root.Make(t.B.Of());
            var g = t.Root.Make(t.A.Of());
            var cnt = t.Graph.DefineFunction<Node, long>("cnt", FunctionMerge.Expression<long>((old, @new) => Primitives.Max.Of(old, @new)));
            var h = t.Graph.DefineFunction<string, long>("h", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, cnt.Of(g))));
            t.Root.Assume(cnt.Of(g).Is(1L), cnt.Of(g2).Is(2L), h.Of("x").Is(10L));
            t.Rules.DefineRule("set", r =>
            {
                r.Then(t.A.Of().Is(t.B.Of()));
                r.Then(h.Of("x").Is(5L));
            });

            t.Root.Run(t.Rules);
            Assert.Equal(11L, t.Root.Get(h.Of("x")));

            t.Root.Assume(h.Of("x").Is(5L));
            Assert.Equal(13L, t.Root.Get(h.Of("x")));
        }

        /// <summary>
        /// A rule's writes fold through a class constant as a host's do, and a union in the same run follows:
        /// (rule () ((set (h 1) (A)) (set (h 1) (B)) (union (C) (D)))) with :merge (C) gives the class of (C) and (D).
        /// So does the rebuild's merge of two rows whose keys a union makes equal.
        /// </summary>
        [Fact]
        public void ARuleAndTheRebuildMergeThroughAClassConstant()
        {
            var t = new NodeTheory();
            var g = t.Root.Make(t.Lit.Of(1L));
            var h = t.Graph.DefineFunction<long, Node>("h", FunctionMerge.Expression<Node>((old, @new) => g));
            var joined = t.Graph.DefineFunction<Node, Node>("joined", FunctionMerge.Expression<Node>((old, @new) => g));
            t.Rules.DefineRule("set", r =>
            {
                r.Then(h.Of(1L).Is(t.A.Of()));
                r.Then(h.Of(1L).Is(t.B.Of()));
                r.Then(t.Lit.Of(1L).Is(t.Lit.Of(2L)));
            });

            t.Root.Run(t.Rules);

            Assert.True(t.Root.AreEqual(t.Root.Get(h.Of(1L)), t.Lit.Of(2L)));
            Assert.True(t.Root.AreEqual(t.Root.Get(h.Of(1L)), g));

            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(joined.Of(a).Is(t.Wrap.Of(a)), joined.Of(b).Is(t.Wrap.Of(b)));
            t.Root.Union(b, a);
            Assert.Equal(1, t.Root.CountRows(joined));
            Assert.True(t.Root.AreEqual(t.Root.Get(joined.Of(a)), g));
        }

        /// <summary>
        /// A child sees what the root's merge of a class constant wrote, also after the child was created.
        /// </summary>
        [Fact]
        public void AChildSeesTheRootsMergeOfAClassConstant()
        {
            var t = new NodeTheory();
            var g = t.Root.Make(t.Lit.Of(1L));
            var h = t.Graph.DefineFunction<string, Node>("h", FunctionMerge.Expression<Node>((old, @new) => g));
            t.Root.Assume(h.Of("x").Is(t.A.Of()));
            var child = t.Root.CreateChild();

            t.Root.Assume(h.Of("x").Is(t.B.Of()));

            Assert.True(child.AreEqual(child.Get(h.Of("x")), g));
            Assert.Throws<InvalidOperationException>(() => child.Assume(h.Of("x").Is(t.A.Of())));
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
        /// The expression is checked when its function is defined: it may hold only its own old and new values, class
        /// constants only from the root, as a global rule's, no table of another graph and no scoped view; and it is a
        /// term, not <c>null</c>. A refused definition leaves no table behind.
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
            var child = t.Root.CreateChild();
            var local = child.Make(t.A.Of());
            var foreignClass = other.Root.Make(other.A.Of());
            var uncommitted = default(Node);
            t.Root.Write(w =>
            {
                uncommitted = w.Make(t.B.Of());
                BatchRejection.FromAnotherThread(w, t.Num.Of(1L));
            });
            var gone = t.Root.CreateChild();
            var stale = gone.Make(t.Lit.Of(3L));
            gone.Dispose();
            var tables = t.Graph.Tables.Count;

            var foreign = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, long>("f", FunctionMerge.Expression<long>((old, @new) => Primitives.Add.Of(old, leaked))));
            var constant = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, Node>("g", FunctionMerge.Expression<Node>((old, @new) => t.Add.Of(old, local))));
            var none = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, Node>("g", FunctionMerge.Expression<Node>((old, @new) => default(Node))));
            var elsewhere = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, Node>("g", FunctionMerge.Expression<Node>((old, @new) => foreignClass)));
            var rejected = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, Node>("g", FunctionMerge.Expression<Node>((old, @new) => uncommitted)));
            var disposed = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, Node>("g", FunctionMerge.Expression<Node>((old, @new) => stale)));
            var graph = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, Node>("i", FunctionMerge.Expression<Node>((old, @new) => other.Add.Of(old, @new))));
            var scoped = Assert.Throws<ArgumentException>(() => t.Graph.DefineFunction<string, long>("j", FunctionMerge.Expression<long>((old, @new) => values.In(t.Graph.RootTag).Of(@new))));
            Assert.Throws<ArgumentException>(() => FunctionMerge.Expression<long>((old, @new) => null!));

            Assert.Contains("is not its old or new value", foreign.Message, StringComparison.Ordinal);
            Assert.Contains("a merge may hold only classes from the root", constant.Message, StringComparison.Ordinal);
            Assert.Contains("default(ClassId), which names no class", none.Message, StringComparison.Ordinal);
            Assert.Contains("belongs to another graph", elsewhere.Message, StringComparison.Ordinal);
            Assert.Contains("was made by a Write batch that did not commit", rejected.Message, StringComparison.Ordinal);
            Assert.Contains("which is disposed", disposed.Message, StringComparison.Ordinal);
            Assert.Contains("a table of another graph", graph.Message, StringComparison.Ordinal);
            Assert.Contains("a merge reads its own scope", scoped.Message, StringComparison.Ordinal);
            Assert.Equal(tables, t.Graph.Tables.Count);
            GC.KeepAlive(child);
        }

    }

}
