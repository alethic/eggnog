using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Merges that are not lattices, egglog's <c>:merge new</c>, <c>:merge old</c> and <c>:merge (f old new)</c>
    /// (<see cref="FunctionMerge"/>): every write folds into the value before it, in order, from host writes, rules and
    /// unions; a merge that throws; and the refusals below the root, where only a lattice merge may write.
    /// </summary>
    public sealed class FunctionMergeTests
    {

        static readonly FunctionMerge<long> Sum = FunctionMerge.Of<long>("sum", (a, b) => checked(a + b));

        [Fact]
        public void NewKeepsTheLastValue()
        {
            var graph = new EGraph();
            var f = graph.DefineFunction<string, long>("f", FunctionMerge.New<long>());
            graph.Root.Assume(f.Of("k").Is(1L));
            graph.Root.Assume(f.Of("k").Is(3L), f.Of("k").Is(2L));

            Assert.Equal(2L, graph.Root.Get(f.Of("k")));
            Assert.Equal("new", ((FunctionHandle)f).Merge!.Name);
        }

        [Fact]
        public void OldKeepsTheFirstValue()
        {
            var graph = new EGraph();
            var f = graph.DefineFunction<string, long>("f", FunctionMerge.Old<long>());
            graph.Root.Assume(f.Of("k").Is(1L));
            var version = graph.Root.Version;
            graph.Root.Assume(f.Of("k").Is(3L), f.Of("k").Is(2L));

            Assert.Equal(1L, graph.Root.Get(f.Of("k")));
            Assert.Equal(version, graph.Root.Version);
        }

        /// <summary>
        /// The merge need be neither idempotent nor commutative: a write of the value already there folds again, and the
        /// writes fold in the order they come, as egglog's <c>set</c> commands do.
        /// </summary>
        [Fact]
        public void HostWritesFoldInOrderEvenWhenTheValueIsTheSame()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var word = graph.DefineFunction<string, string>("word", FunctionMerge.Of<string>("append", (a, b) => a + b));
            graph.Root.Assume(sum.Of("k").Is(2L), sum.Of("k").Is(2L), word.Of("k").Is("a"), word.Of("k").Is("b"));
            graph.Root.Assume(sum.Of("k").Is(2L), word.Of("k").Is("c"));

            Assert.Equal(6L, graph.Root.Get(sum.Of("k")));
            Assert.Equal("abc", graph.Root.Get(word.Of("k")));
        }

        /// <summary>
        /// A read in the batch sees the batch's earlier writes, merged: <c>(set (f k) (+ (f k) 1))</c> after
        /// <c>(set (f k) 2)</c> writes 3, which the sum merges into 5.
        /// </summary>
        [Fact]
        public void AReadInTheBatchSeesTheMergedValue()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            graph.Root.Assume(sum.Of("k").Is(2L), sum.Of("k").Is(Primitives.Add.Of(sum.Of("k"), 1L)));

            Assert.Equal(5L, graph.Root.Get(sum.Of("k")));
        }

        [Fact]
        public void RulesFoldEveryMatchIntoTheValue()
        {
            var t = new NodeTheory();
            var sum = t.Graph.DefineFunction<string, long>("sum", Sum);
            t.Rules.DefineRule("total", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(sum.Of("total").Is(x));
            });
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(6L, t.Root.Get(sum.Of("total")));
        }

        /// <summary>
        /// A union that makes two keys equal merges their values with the merge, as egglog's rebuild does, instead of
        /// the conflict a function without a merge gives.
        /// </summary>
        [Fact]
        public void AUnionMergesTheValuesOfTheKeysItMakesEqual()
        {
            var t = new NodeTheory();
            var sum = t.Graph.DefineFunction<Node, long>("sum", Sum);
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(sum.Of(a).Is(1L), sum.Of(b).Is(2L));

            t.Root.Union(a, b);

            Assert.Equal(3L, t.Root.Get(sum.Of(a)));
            Assert.Equal(1, t.Root.CountRows(sum));
        }

        /// <summary>
        /// A union's rebuild merges the row it keeps, the smaller class's, as the old value with the other row as the
        /// new, as egglog's rebuild does: <c>new</c> keeps the other row's value, and <c>old</c> the kept row's.
        /// </summary>
        [Fact]
        public void AUnionMergesTheKeptRowAsTheOldValue()
        {
            var t = new NodeTheory();
            var newest = t.Graph.DefineFunction<Node, long>("newest", FunctionMerge.New<long>());
            var oldest = t.Graph.DefineFunction<Node, long>("oldest", FunctionMerge.Old<long>());
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(newest.Of(a).Is(1L), newest.Of(b).Is(2L), oldest.Of(a).Is(1L), oldest.Of(b).Is(2L));

            t.Root.Union(b, a);

            Assert.Equal(2L, t.Root.Get(newest.Of(a)));
            Assert.Equal(1L, t.Root.Get(oldest.Of(a)));
        }

        /// <summary>
        /// A write of such a function of classes after the batch merged classes comes after the union's rebuild, as
        /// egglog's next command does: (set (newest (A)) 5) (set (newest (B)) 7) (union (A) (B)) (set (newest (A)) 1)
        /// leaves 1. A write before the merge keeps egglog's order.
        /// </summary>
        [Fact]
        public void ABatchWritesAFunctionOfClassesWithAMergeAfterAMerge()
        {
            var t = new NodeTheory();
            var newest = t.Graph.DefineFunction<Node, long>("newest", FunctionMerge.New<long>());
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(newest.Of(a).Is(5L), newest.Of(b).Is(7L));

            t.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(newest.Of(a).Is(1L));
            });

            Assert.True(t.Root.AreEqual(a, t.B.Of()));
            Assert.Equal(1L, t.Root.Get(newest.Of(a)));

            // Written first, the value is merged by the union's rebuild after it, as egglog's next command would: the
            // kept row, 2, is old, and the other, 9, new.
            var c = t.Root.Make(t.Lit.Of(1L));
            var d = t.Root.Make(t.Lit.Of(2L));
            t.Root.Assume(newest.Of(d).Is(9L));
            t.Root.Write(w =>
            {
                w.Assume(newest.Of(c).Is(2L));
                w.Union(c, d);
            });
            Assert.Equal(9L, t.Root.Get(newest.Of(c)));
        }

        [Fact]
        public void AMergeThatThrowsWhileAHostWriteIsCheckedWritesNothingAndDoesNotFault()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            graph.Root.Assume(sum.Of("k").Is(long.MaxValue));
            var version = graph.Root.Version;

            var e = Assert.Throws<RuleEvaluationException>(() => graph.Root.Assume(sum.Of("k").Is(1L)));

            Assert.IsType<OverflowException>(e.InnerException);
            Assert.Contains("merge 'sum' of 'sum'", e.Message);
            Assert.False(graph.Root.IsFaulted);
            Assert.Equal(long.MaxValue, graph.Root.Get(sum.Of("k")));
            Assert.Equal(version, graph.Root.Version);
        }

        [Fact]
        public void AMergeThatThrowsDuringARulesMergeFailsTheRun()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("add one", r => r.Then(sum.Of("k").Is(1L)));
            graph.Root.Assume(sum.Of("k").Is(long.MaxValue));

            var e = Assert.Throws<RuleEvaluationException>(() => graph.Root.Run(rules));

            Assert.IsType<OverflowException>(e.InnerException);
            Assert.Contains("merge 'sum'", e.Message);
            Assert.False(graph.Root.IsFaulted);
        }

        [Fact]
        public void AChildCannotWriteAFunctionWithAMerge()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            graph.Root.Assume(sum.Of("k").Is(1L));
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<InvalidOperationException>(() => child.Assume(sum.Of("k").Is(2L)));

            Assert.Contains("only the root may write it", e.Message);
            Assert.Equal(1L, child.Get(sum.Of("k")));
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// A child's run of a rule that writes such a function is refused before any work, whether or not the rule would
        /// match, and the child is not faulted.
        /// </summary>
        [Fact]
        public void AChildCannotRunARuleThatWritesAFunctionWithAMerge()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("add one", r => r.Then(sum.Of("k").Is(1L)));
            graph.Root.Assume(sum.Of("k").Is(1L));
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<InvalidOperationException>(() => child.Run(rules));

            Assert.Contains("Rule 'add one' writes 'sum', whose merge 'sum' is not a lattice", e.Message);
            Assert.False(child.IsFaulted);
            Assert.Equal(1L, child.Get(sum.Of("k")));
            graph.Root.Run(rules);
            Assert.Equal(2L, graph.Root.Get(sum.Of("k")));
        }

        [Fact]
        public void ARuleLocalToAChildCannotWriteAFunctionWithAMerge()
        {
            var graph = new EGraph();
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var rules = graph.DefineRuleset("rules");
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<RuleDefinitionException>(() => child.DefineRule(rules, "add one", r => r.Then(sum.Of("k").Is(1L))));

            Assert.Contains("a rule local to", e.Message);
            Assert.Empty(child.LocalRules);
            graph.Root.DefineRule(rules, "add one", r => r.Then(sum.Of("k").Is(1L)));
            graph.Root.Run(rules);
            Assert.Equal(1L, graph.Root.Get(sum.Of("k")));
        }

        /// <summary>
        /// A child's union in which a class that keys a row of such a function loses is refused before anything is
        /// staged, since the engine keeps the smaller id and the child's rebuild would rewrite the rows of the larger;
        /// directly, or through congruence. A union that such a class wins rewrites none of its rows, and is not refused.
        /// </summary>
        [Fact]
        public void AChildCannotMergeAwayAClassThatKeysAFunctionWithAMerge()
        {
            var t = new NodeTheory();
            var sum = t.Graph.DefineFunction<Node, long>("sum", Sum);
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            t.Root.Make(t.Wrap.Of(one));
            var wrapped = t.Root.Make(t.Wrap.Of(two));
            var later = t.Root.Make(t.Lit.Of(3L));
            t.Root.Assume(sum.Of(b).Is(2L), sum.Of(wrapped).Is(3L), sum.Of(one).Is(1L));
            var child = t.Root.CreateChild();

            var direct = Assert.Throws<InvalidOperationException>(() => child.Union(a, b));
            var congruent = Assert.Throws<InvalidOperationException>(() => child.Union(one, two));

            Assert.Contains("this merge of classes in", direct.Message);
            Assert.Contains("'sum' has the merge 'sum'", congruent.Message);
            Assert.False(child.IsFaulted);
            Assert.False(child.AreEqual(a, t.B.Of()));
            Assert.Equal(3, child.CountRows(sum));

            Assert.True(child.Union(one, later));
            Assert.Equal(1L, child.Get(sum.Of(later)));
            Assert.Equal(3, child.CountRows(sum));
        }

        /// <summary>
        /// A root write keyed by a class that a live child has merged away needs the child's catch-up to rewrite the row,
        /// which the engine refuses: the child is faulted at its next call, and the root is not.
        /// </summary>
        [Fact]
        public void ARootWriteKeyedByAClassAChildMergedAwayFaultsTheChild()
        {
            var t = new NodeTheory();
            var sum = t.Graph.DefineFunction<Node, long>("sum", Sum);
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var child = t.Root.CreateChild();
            child.Union(a, b);

            t.Root.Assume(sum.Of(b).Is(1L));
            var e = Assert.Throws<InvalidOperationException>(() => child.CountRows(sum));

            Assert.Contains("Table sum has a merge that is not a lattice merge", e.Message);
            Assert.True(child.IsFaulted);
            Assert.False(t.Root.IsFaulted);
            Assert.Equal(1L, t.Root.Get(sum.Of(b)));
        }

        [Fact]
        public void ASplitCannotAssumeAFunctionWithAMerge()
        {
            var graph = new EGraph();
            var choice = graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var sum = graph.DefineFunction<string, long>("sum", Sum);

            var e = Assert.Throws<RuleDefinitionException>(() => graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                var v = s.Cases("v", d);
                s.Assume(sum.Of(k).Is(v));
            }));

            Assert.Contains("a case is a child", e.Message);
        }

        /// <summary>
        /// A root run whose split runs a rule that writes such a function in its cases is refused before any work; the
        /// same rule may run at the root outside the split.
        /// </summary>
        [Fact]
        public void ARootRunCannotRunARuleThatWritesAFunctionWithAMergeInASplitsCases()
        {
            var graph = new EGraph();
            var choice = graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var split = graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<long>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)));
            });
            var adds = graph.DefineRuleset("adds");
            adds.DefineRule("add one", r => r.Then(sum.Of("k").Is(1L)));
            var none = graph.DefineRuleset("none");
            graph.Root.Assume(choice.Of("k").Is(Set.Of(1L, 2L)));

            var e = Assert.Throws<InvalidOperationException>(() => graph.Root.Run(Schedule.Split(split, Schedule.Run(adds))));

            Assert.Contains("the cases of a split are children", e.Message);
            Assert.False(graph.Root.IsFaulted);
            Assert.False(graph.Root.TryGet(sum.Of("k"), out _));
            graph.Root.Run(Schedule.Sequence(Schedule.Run(adds), Schedule.Split(split, Schedule.Run(none))));
            Assert.Equal(1L, graph.Root.Get(sum.Of("k")));
        }

        /// <summary>
        /// A rule's union below the root that would rewrite a row of such a function depends on the data, so the run
        /// cannot refuse it before any work: the engine refuses the rewrite, and the run fails with its exception. Here a
        /// split's case does it, and the root is neither faulted nor changed.
        /// </summary>
        [Fact]
        public void AUnionInACaseThatWouldRewriteARowOfAFunctionWithAMergeFailsTheRun()
        {
            var t = new NodeTheory();
            var choice = t.Graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var sum = t.Graph.DefineFunction<Node, long>("sum", Sum);
            var split = t.Graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<long>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)));
            });
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(sum.Of(a).Is(1L), sum.Of(b).Is(2L), choice.Of("k").Is(Set.Of(1L, 2L)));
            t.Rules.DefineRule("join", r =>
            {
                r.When(choice.Of("k").Is(Set.Of(1L)));
                r.Then(t.A.Of().Is(t.B.Of()));
            });

            var e = Assert.Throws<InvalidOperationException>(() => t.Root.Run(Schedule.Split(split, Schedule.Run(t.Rules))));

            Assert.Contains("Table sum has a merge that is not a lattice merge", e.Message);
            Assert.False(t.Root.IsFaulted);
            Assert.Equal(2, t.Root.CountRows(sum));
            Assert.Empty(t.Root.Children);
        }

        [Fact]
        public void ARuleCannotReadAFunctionWithAMergeInAnotherScopeButAHostReadCan()
        {
            var graph = new EGraph();
            var outer = graph.DefineScopeTag("outer");
            var sum = graph.DefineFunction<string, long>("sum", Sum);
            var copy = graph.DefineFunction<string, long>("copy", ValueLattice.Max<long>());
            var rules = graph.DefineRuleset("rules");

            var e = Assert.Throws<RuleDefinitionException>(() => rules.DefineRule("copy", r =>
            {
                var (k, v) = r.Vars<string, long>("k", "v");
                r.When(v.Is(sum.In(outer).Of(k)));
                r.Then(copy.Of(k).Is(v));
            }));

            Assert.Contains("'sum' has the merge 'sum', which is not a lattice", e.Message);

            // A host read reads the scope itself, with no mirror.
            graph.Root.Assume(sum.Of("k").Is(4L));
            Assert.Equal(4L, graph.Root.CreateChild().Get(sum.In(graph.RootTag).Of("k")));
        }

        [Fact]
        public void TheMergeMustNotBeADefaultValue()
        {
            var graph = new EGraph();

            Assert.Throws<ArgumentException>(() => graph.DefineFunction<string, long>("f", default(FunctionMerge<long>)));
            Assert.Throws<ArgumentNullException>(() => FunctionMerge.Of<long>("m", null!));
        }

    }

}
