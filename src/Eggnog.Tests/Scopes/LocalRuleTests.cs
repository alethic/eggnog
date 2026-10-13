using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Cosmos.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Local rules (§6.7): active in their scope and its descendants, fixed once it freezes, freed when it is released.
    /// </summary>
    public sealed class LocalRuleTests
    {

        /// <summary>
        /// Two sibling containers under a frozen root, each with a local rule of its own.
        /// </summary>
        static (ScopeTheory T, Scope A, Scope B, Rule RuleA) Siblings()
        {
            var t = new ScopeTheory();
            t.Root.Freeze();
            var a = t.Root.CreateChild(t.Outer);
            var b = t.Root.CreateChild(t.Outer);
            var ruleA = a.DefineRule(t.Rules, "a sees", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            return (t, a, b, ruleA);
        }

        [Fact]
        public void ARuleLocalToOneContainerNeverFiresInItsSibling()
        {
            var (t, a, b, ruleA) = Siblings();
            a.Assume(t.Fact.Of("x"));
            b.Assume(t.Fact.Of("x"));

            var inA = a.Run(Schedule.Saturate(t.Rules));
            var inB = b.Run(Schedule.Saturate(t.Rules));

            Assert.True(a.Check(t.Seen.Of("x")));
            Assert.False(b.Check(t.Seen.Of("x")));
            Assert.Equal(1, inA.Rules[ruleA].Matches);
            Assert.False(inB.Rules.ContainsKey(ruleA));
            Assert.Same(a, ruleA.Owner);
            Assert.Equal(new[] { ruleA }, a.LocalRules);
            Assert.Empty(b.LocalRules);
            Assert.Empty(t.Rules.Rules);
            Assert.StartsWith(a.Path + ":rules/a sees#", ruleA.Compiled.Variants.Default!.Description);
        }

        [Fact]
        public void ALocalRuleIsActiveInDescendants()
        {
            var (t, a, _, ruleA) = Siblings();
            a.Freeze();
            var child = a.CreateChild(t.Inner);
            child.Assume(t.Fact.Of("y"));

            var report = child.Run(Schedule.Saturate(t.Rules));

            Assert.True(child.Check(t.Seen.Of("y")));
            Assert.False(a.Check(t.Seen.Of("y")));
            Assert.Equal(1, report.Rules[ruleA].Matches);
        }

        [Fact]
        public void ALocalRuleIsGoneAfterItsScopeIsReleased()
        {
            var (t, a, _, ruleA) = Siblings();
            var variant = ruleA.Compiled.Variants.Default!;
            Assert.True(t.Graph.VariantsByDescription.ContainsKey(variant.Description));

            a.Dispose();

            Assert.False(t.Graph.VariantsByDescription.ContainsKey(variant.Description));
            Assert.Null(ruleA.Compiled.Variants.Default);
            Assert.Throws<ArgumentException>(() => t.Graph.Engine.GetRuleInfo(variant.Id));
        }

        [Fact]
        public void DefineRuleOnAFrozenScopeThrows()
        {
            var (t, a, _, _) = Siblings();
            a.Freeze();
            Assert.Equal($"Scope '{a.Path}' is frozen; its local rules are fixed",
                Assert.Throws<InvalidOperationException>(() => a.DefineRule(t.Rules, "late", r => r.When(t.Fact.Of("x")))).Message);
            Assert.Single(a.LocalRules);
        }

        [Fact]
        public void DefineRuleOfAnotherGraphsRulesetThrows()
        {
            var (_, a, _, _) = Siblings();
            var other = new ScopeTheory();
            Assert.Throws<ArgumentException>(() => a.DefineRule(other.Rules, "foreign", r => r.When(other.Fact.Of("x"))));
        }

        [Fact]
        public async Task DefineRuleFromAnotherThreadWhileTheScopeRunsThrows()
        {
            var t = new ScopeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            t.DefineBlocking(entered, release);
            var scope = t.Root.CreateChild(t.Outer);
            scope.Assume(t.Fact.Of("a"));
            var run = Task.Factory.StartNew(() => scope.Run(t.Rules), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

            Assert.Contains("in use on another thread",
                Assert.Throws<InvalidOperationException>(() => scope.DefineRule(t.Rules, "concurrent", r => r.When(t.Fact.Of("a")))).Message);

            release.Set();
            await run;
            Assert.Empty(scope.LocalRules);
            scope.DefineRule(t.Rules, "after", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Fact.Of("after"));
            });
            scope.Run(Schedule.Saturate(t.Rules));
            Assert.True(scope.Check(t.Fact.Of("after")));
        }

        [Fact]
        public void OneCosmosTheoryServesTwoContainers()
        {
            const string Ref = "123e4567-e89b-12d3-a456-426614174000";
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();

            // Orders carry a UUID ref in container one; invoices do in container two.
            var one = graph.Root.CreateChild(c.Schema);
            one.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("order", "invoice")));
            c.DeclareBranch(one, "kind", "order", c.Form.Of("ref", "uuid-lower"));
            one.Run(Schedule.Saturate(c.Horn, c.Lattice));
            one.Freeze();
            var two = graph.Root.CreateChild(c.Schema);
            two.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("order", "invoice")));
            c.DeclareBranch(two, "kind", "invoice", c.Form.Of("ref", "uuid-lower"));
            two.Run(Schedule.Saturate(c.Horn, c.Lattice));
            two.Freeze();

            foreach (var (schema, kind, lowers) in new[] { (one, "order", true), (one, "invoice", false), (two, "order", false), (two, "invoice", true) })
            {
                using var query = schema.CreateChild(c.Query);
                var q = query.Make(c.And.Of(c.StrEq.Of("kind", kind), c.UuidEq.Of("ref", Ref)));
                query.Assume(c.WhereClause.Of(q));
                query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
                Assert.Equal(lowers, query.Check(c.Form.Of("ref", "uuid-lower")));
                Assert.Equal(lowers, query.Extract(q).Mentions(c.UuidEq) == false);
            }
        }

    }

}
