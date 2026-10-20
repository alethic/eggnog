using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Subsumption
{

    /// <summary>
    /// Subsumption by rules, ported from egglog's tests (<c>tests/integration_test.rs</c>,
    /// <c>tests/subsume-relation.egg</c>, <c>tests/term-encoding-subsume-rebuild-bug.egg</c> and
    /// <c>tests/web-demo/subsume.egg</c> at 5e655cae): a subsumed row is matched by no later rule body or query, but
    /// checks and lookups still find it, rebuilds keep it subsumed, and a subsumption made by one rule of an iteration
    /// takes effect from the next iteration.
    /// </summary>
    public sealed class SubsumeRuleTests
    {

        [Fact]
        public void ASubsumedTermIsNotRewritten()
        {
            // test_subsume: commutativity rewrites (Add 3 4) but not the subsumed (Add 1 2).
            var t = new MathTheory();
            t.Rules.DefineRule("commute", r =>
            {
                var (a, b) = r.Vars<Node, Node>("a", "b");
                r.Rewrite(t.Add.Of(a, b), t.Add.Of(b, a));
            });
            var x = t.Root.Make(t.Add.Of(t.Num.Of(1L), t.Num.Of(2L)));
            var y = t.Root.Make(t.Add.Of(t.Num.Of(3L), t.Num.Of(4L)));
            t.Root.Subsume(t.Add.Of(t.Num.Of(1L), t.Num.Of(2L)));

            t.Root.Run(t.Rules);

            Assert.True(t.Root.AreEqual(y, t.Add.Of(t.Num.Of(4L), t.Num.Of(3L))));
            Assert.False(t.Root.TryGet(t.Add.Of(t.Num.Of(2L), t.Num.Of(1L)), out _));

            // Something equal to x that can be extracted, and costs more than x's term: the subsumed term cannot.
            var other = t.Graph.DefineConstructor<Node>("otherConst", 100);
            t.Root.Union(x, t.Root.Make(other.Of()));
            Assert.Equal("(otherConst)", t.Root.Extract(x).ToString());
        }

        [Fact]
        public void ASubsumingRewriteIsNotRewrittenAgain()
        {
            // test_rewrite_subsumed: once (most-exp) is rewritten with :subsume, a later rewrite of it never fires.
            var t = new MathTheory();
            var expensive = t.Graph.DefineConstructor<Node>("expensive", 100, MathTheory.Subsumable);
            var mostExpensive = t.Graph.DefineConstructor<Node>("most-exp", 1000, MathTheory.Subsumable);
            var cheap = t.Graph.DefineConstructor<Node>("cheap", 1, MathTheory.Subsumable);
            var first = t.Graph.DefineRuleset("first");
            first.DefineRule("most to expensive", r => r.Rewrite(mostExpensive.Of(), expensive.Of(), subsume: true));
            var second = t.Graph.DefineRuleset("second");
            second.DefineRule("most to cheap", r => r.Rewrite(mostExpensive.Of(), cheap.Of()));
            var term = t.Root.Make(mostExpensive.Of());

            t.Root.Run(first);
            t.Root.Run(second);

            Assert.True(t.Root.AreEqual(term, expensive.Of()));
            Assert.False(t.Root.TryGet(cheap.Of(), out _));
            Assert.Equal("(expensive)", t.Root.Extract(term).ToString());
        }

        [Fact]
        public void TheWebDemoExpandsTimesThreeAndKeepsTheProduct()
        {
            // web-demo/subsume.egg: multiplying by three is expensive, so it is rewritten to two additions and subsumed.
            var t = new MathTheory();
            t.Rules.DefineRule("times three", r =>
            {
                var x = r.Var<Node>("x");
                r.Rewrite(t.Mul.Of(t.Num.Of(3L), x), t.Add.Of(x, t.Add.Of(x, x)), subsume: true);
            });
            var expression = t.Root.Make(t.Mul.Of(t.Num.Of(2L), t.Mul.Of(t.Num.Of(3L), t.Var.Of("x"))));

            t.Root.Run(Schedule.Repeat(10, t.Rules));

            var expanded = t.Mul.Of(t.Num.Of(2L), t.Add.Of(t.Var.Of("x"), t.Add.Of(t.Var.Of("x"), t.Var.Of("x"))));
            Assert.True(t.Root.AreEqual(expression, expanded));
            Assert.Equal("(Mul (Num 2) (Add (Var \"x\") (Add (Var \"x\") (Var \"x\"))))", t.Root.Extract(expression).ToString());

            // Even though it cannot be extracted, the product is still there: checks see subsumed rows.
            Assert.True(t.Root.AreEqual(expression, t.Mul.Of(t.Num.Of(2L), t.Mul.Of(t.Num.Of(3L), t.Var.Of("x")))));
            Assert.True(t.Root.Check(Fact.Exists(t.Mul.Of(t.Num.Of(3L), t.Var.Of("x")))));

            // Commutativity of multiplication never reaches the subsumed product.
            var commute = t.Graph.DefineRuleset("commute");
            commute.DefineRule("commute mul", r =>
            {
                var (a, b) = r.Vars<Node, Node>("a", "b");
                r.Rewrite(t.Mul.Of(a, b), t.Mul.Of(b, a));
            });
            t.Root.Run(Schedule.Repeat(10, commute));

            Assert.False(t.Root.TryGet(t.Mul.Of(t.Var.Of("x"), t.Num.Of(3L)), out _));
            var extracted = t.Root.Extract(expression).ToString();
            Assert.Contains("(Add (Var \"x\") (Add (Var \"x\") (Var \"x\")))", extracted, StringComparison.Ordinal);
            Assert.DoesNotContain("(Num 3)", extracted, StringComparison.Ordinal);
        }

        [Fact]
        public void RebuiltRowsStaySubsumed()
        {
            // term-encoding-subsume-rebuild-bug.egg: a union that rewrites the subsumed row's key keeps it subsumed.
            var graph = new EGraph();
            graph.DefineSort<Node>("X");
            var init = graph.DefineConstructor<Node>("Init", 1, MathTheory.Subsumable);
            var init2 = graph.DefineConstructor<Node>("Init2", 1, MathTheory.Subsumable);
            var fn = graph.DefineConstructor<Node, Node>("Fn", 1, MathTheory.Subsumable);
            var matched = graph.DefineRelation("matched");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("match", r =>
            {
                r.When(Fact.Exists(fn.Of(init2.Of())));
                r.Then(matched.Of());
            });
            var root = graph.Root;
            var second = root.Make(init2.Of());
            root.Make(fn.Of(init.Of()));
            root.Subsume(fn.Of(init.Of()));
            root.Union(second, root.Get(init.Of()));

            root.Run(rules);

            Assert.False(root.Check(matched.Of()));
            Assert.True(Assert.Single(root.Rows(fn)).Subsumed);
        }

        [Fact]
        public void ACheckFindsASubsumedRelationRow()
        {
            // subsume-relation.egg: the subsumed row (R A B) survives the union of B and C, which merges it with (R A C).
            var graph = new EGraph();
            graph.DefineSort<Node>("V");
            var a = graph.DefineConstructor<Node>("A");
            var b = graph.DefineConstructor<Node>("B");
            var c = graph.DefineConstructor<Node>("C");
            var r = graph.DefineRelation<Node, Node>("R", MathTheory.Subsumable);
            var root = graph.Root;
            var (av, bv, cv) = (root.Make(a.Of()), root.Make(b.Of()), root.Make(c.Of()));
            root.Subsume(r.Of(av, bv));
            root.Assume(r.Of(av, cv));
            root.Union(bv, cv);

            Assert.True(root.Check(r.Of(a.Of(), b.Of())));
            var row = Assert.Single(root.Rows(r));
            Assert.True(row.Subsumed);
        }

        [Fact]
        public void SubsumptionTakesEffectFromTheNextIteration()
        {
            // Actions are staged and merged after the iteration's search, so a rule of the same iteration still matches
            // the row another rule subsumes; later iterations and later rules do not.
            var t = new MathTheory();
            t.Rules.DefineRule("prune", r =>
            {
                var n = r.Var<long>("n");
                r.When(Fact.Exists(t.Num.Of(n)));
                r.Subsume(t.Num.Of(n));
            });
            t.DefineSee();
            var later = t.Graph.DefineRelation<long>("later");
            var after = t.Graph.DefineRuleset("after");
            after.DefineRule("see later", r =>
            {
                var n = r.Var<long>("n");
                r.When(Fact.Exists(t.Num.Of(n)));
                r.Then(later.Of(n));
            });
            t.Root.Make(t.Num.Of(1L));

            var report = t.Root.Run(Schedule.Saturate(t.Rules));
            t.Root.Run(Schedule.Saturate(after));

            Assert.True(t.Root.Check(t.Seen.Of(1L)));
            Assert.False(t.Root.Check(later.Of(1L)));
            Assert.True(Assert.Single(t.Root.Rows(t.Num)).Subsumed);
            Assert.Equal(1L, report.Rules.Values.Single(s => s.Rule.Name == "prune").Matches);
        }

        [Fact]
        public void ARuleSubsumesATermItMakes()
        {
            // test_subsume_ok's "subsuming a non-existent tuple", from a rule: the term is made, subsumed, and its class
            // has no other member, so nothing can be extracted from it.
            var t = new MathTheory();
            t.Rules.DefineRule("make subsumed", r =>
            {
                var n = r.Var<long>("n");
                r.When(t.Seen.Of(n));
                r.Subsume(t.Num.Of(n));
            });
            t.Root.Assume(t.Seen.Of(7L));

            t.Root.Run(t.Rules);

            var made = t.Root.Get(t.Num.Of(7L));
            Assert.True(Assert.Single(t.Root.Rows(t.Num)).Subsumed);
            Assert.False(t.Root.CreateExtractor().TryExtract(made, out _));
            Assert.Throws<InvalidOperationException>(() => t.Root.Extract(made));
        }

        [Fact]
        public void ARuleSubsumesRelationRowsAndGoesOnWithItsHead()
        {
            // A head that inserts a row and then subsumes it, and one that subsumes rows missing until then: each row ends
            // subsumed, and the head's later items still run. egglog at 5e655cae halts the match at a missing row and
            // loses a subsumption an earlier lookup predicted (its defect 19); the engine's port fixes both.
            var t = new MathTheory();
            var fact = t.Graph.DefineRelation<long>("fact", MathTheory.Subsumable);
            var after = t.Graph.DefineRelation<long>("after");
            t.Rules.DefineRule("flag", r =>
            {
                var n = r.Var<long>("n");
                r.When(t.Seen.Of(n));
                r.Then(fact.Of(n));
                r.Subsume(fact.Of(n));
                r.Subsume(t.Num.Of(n));
                r.Then(after.Of(n));
            });
            var query = t.Graph.DefineQuery("facts", q => q.When(fact.Of(q.Var<long>("n"))));
            t.Root.Assume(t.Seen.Of(1L), t.Seen.Of(2L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(new[] { (1L, true), (2L, true) }, t.Root.Rows(fact).Select(row => ((long)row.Keys[0], row.Subsumed)));
            Assert.Equal(2L, t.Root.CountRows(t.Num));
            Assert.All(t.Root.Rows(t.Num), row => Assert.True(row.Subsumed));
            Assert.True(t.Root.Check(after.Of(1L), after.Of(2L)));
            Assert.Equal(0, t.Root.Count(query));
        }

        [Fact]
        public void ASubsumingRewriteSubsumesTheRowItsBodyMatched()
        {
            // The left side reads a function; the subsumption takes the left side's arguments from the match, so it is
            // allowed although a head may not read a function, and it subsumes exactly the matched row.
            var t = new MathTheory();
            var alias = t.Graph.DefineFunction<long, long>("alias");
            t.Rules.DefineRule("name aliased", r =>
            {
                var n = r.Var<long>("n");
                r.Rewrite(t.Num.Of(alias.Of(n)), t.Var.Of("aliased"), subsume: true);
            });
            t.Root.Assume(alias.Of(1L).Is(5L));
            var five = t.Root.Make(t.Num.Of(5L));
            t.Root.Make(t.Num.Of(6L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.AreEqual(five, t.Var.Of("aliased")));
            Assert.Equal(new[] { (5L, true), (6L, false) }, t.Root.Rows(t.Num).Select(row => ((long)row.Keys[0], row.Subsumed)));
            Assert.Equal("(Var \"aliased\")", t.Root.Extract(five).ToString());
        }

        [Fact]
        public void ASubsumingRewriteWithAConditionSubsumesOnlyWhereItHolds()
        {
            // egglog's (rewrite lhs rhs :when (cond) :subsume): the condition joins the body.
            var t = new MathTheory();
            t.Rules.DefineRule("times three", r =>
            {
                var x = r.Var<Node>("x");
                r.When(Fact.Exists(t.Var.Of("go")));
                r.Rewrite(t.Mul.Of(t.Num.Of(3L), x), t.Add.Of(x, t.Add.Of(x, x)), subsume: true);
            });
            t.Root.Make(t.Mul.Of(t.Num.Of(3L), t.Var.Of("y")));

            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(Assert.Single(t.Root.Rows(t.Mul)).Subsumed);

            t.Root.Make(t.Var.Of("go"));
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(Assert.Single(t.Root.Rows(t.Mul)).Subsumed);
        }

        [Fact]
        public void LookupsSeeSubsumedRowsAndKeepThemSubsumed()
        {
            // A head's get-or-add finds the subsumed term (egglog's lookups see subsumed rows), and does not unsubsume it.
            var t = new MathTheory();
            var wrapped = t.Graph.DefineRelation<Node>("wrapped");
            t.Rules.DefineRule("wrap", r =>
            {
                var n = r.Var<long>("n");
                r.When(t.Seen.Of(n));
                r.Then(wrapped.Of(t.Num.Of(n)));
            });
            var num = t.Root.Make(t.Num.Of(3L));
            t.Root.Subsume(t.Num.Of(3L));
            t.Root.Assume(t.Seen.Of(3L));

            t.Root.Run(t.Rules);

            Assert.True(t.Root.Check(wrapped.Of(num)));
            Assert.True(Assert.Single(t.Root.Rows(t.Num)).Subsumed);
            Assert.True(t.Root.AreEqual(num, t.Num.Of(3L)));
        }

        [Fact]
        public void QueriesSkipSubsumedRowsAndChecksDoNot()
        {
            var t = new MathTheory();
            var fact = t.Graph.DefineRelation<string>("fact", MathTheory.Subsumable);
            var query = t.Graph.DefineQuery("facts", q => q.When(fact.Of(q.Var<string>("x"))));
            t.Root.Assume(fact.Of("a"), fact.Of("b"));
            t.Root.Subsume(fact.Of("b"));

            Assert.Equal(1, t.Root.Count(query));
            Assert.True(t.Root.Check(fact.Of("a"), fact.Of("b")));
            Assert.Equal(2L, t.Root.CountRows(fact));
            Assert.Equal(new[] { ("a", false), ("b", true) }, t.Root.Rows(fact).Select(row => ((string)row.Keys[0], row.Subsumed)));
        }

    }

}
