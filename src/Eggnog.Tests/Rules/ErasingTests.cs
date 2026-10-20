using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// Erasing rules, which keep a conjunct from proving itself redundant: a ruleset's licence makes every rule
    /// erasing; a rule reads fact tables only through the licence scope unless it says why it may read its own; term
    /// and analysis tables are unrestricted.
    /// </summary>
    public sealed class ErasingTests
    {

        /// <summary>
        /// A theory with a sort, a constructor, an analysis relation and a ruleset licensed under the outer tag.
        /// </summary>
        static (ScopeTheory T, Ruleset Licensed, Constructor<string, Node> Term, Relation<string> Analysis) Theory()
        {
            var t = new ScopeTheory();
            t.Graph.DefineSort<Node>("Node");
            var term = t.Graph.DefineConstructor<string, Node>("Term");
            var analysis = t.Graph.DefineRelation<string>("mentioned", new TableOptions { Role = TableRole.Analysis });
            var licensed = t.Graph.DefineRuleset("licensed", new RulesetOptions { Licence = t.Outer });
            return (t, licensed, term, analysis);
        }

        [Fact]
        public void ARulesetLicenceMakesEveryRuleErasing()
        {
            var (t, licensed, _, _) = Theory();
            Assert.Same(t.Outer, licensed.Licence);
            Assert.Null(t.Rules.Licence);

            var rule = licensed.DefineRule("through the licence", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.In(t.Outer).Of(x));
                r.Then(t.Seen.Of(x));
            });

            Assert.Same(t.Outer, rule.Licence);
            Assert.Null(rule.OwnFactsReason);
            Assert.Equal(new[] { t.Outer }, rule.ReadScopes);
            Assert.EndsWith(":ruleset licensed :erasing outer)", rule.ToString());
        }

        [Fact]
        public void ARewriteReadingTheRunningScopesFactsIsRejectedWithoutAnExemption()
        {
            var (t, licensed, term, _) = Theory();
            var e = Assert.Throws<RuleDefinitionException>(() => licensed.DefineRule("deletes", r =>
            {
                var (p, n) = r.Vars<string, Node>("p", "n");
                r.When(n.Is(term.Of(p)), t.Fact.Of(p));
                r.Then(n.Is(term.Of("deleted")));
            }));
            Assert.Contains("rule reads fact table 'fact' outside scope 'outer'", e.Message);

            // The contradiction relation is a fact table too.
            e = Assert.Throws<RuleDefinitionException>(() => licensed.DefineRule("contradicted", r =>
            {
                var n = r.Var<Node>("n");
                r.When(n.Is(term.Of("a")), t.Graph.Contradiction.Of());
                r.Then(n.Is(term.Of("false")));
            }));
            Assert.Contains("rule reads fact table '$contradiction' outside scope 'outer'", e.Message);
        }

        [Fact]
        public void ReadsOwnFactsExemptsAndRenders()
        {
            var (t, licensed, term, _) = Theory();
            var rule = licensed.DefineRule("judges own facts", r =>
            {
                var (p, n) = r.Vars<string, Node>("p", "n");
                r.ReadsOwnFacts("the guard stays in the predicate");
                r.When(n.Is(term.Of(p)), t.Fact.Of(p));
                r.Then(n.Is(term.Of("lowered")));
            });

            Assert.Null(rule.Licence);
            Assert.Equal("the guard stays in the predicate", rule.OwnFactsReason);
            Assert.EndsWith(":ruleset licensed :reads-own-facts \"the guard stays in the predicate\")", rule.ToString());
        }

        [Fact]
        public void ErasingMakesOneRuleOfAnUnlicensedRulesetErasing()
        {
            var (t, licensed, _, _) = Theory();
            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("one erasing", r =>
            {
                var x = r.Var<string>("x");
                r.Erasing(t.Inner);
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            }));
            Assert.Equal("rule reads fact table 'fact' outside scope 'inner'; read it through In(inner), or call ReadsOwnFacts with the reason that is sound", e.Message);

            var rule = t.Rules.DefineRule("one erasing", r =>
            {
                var x = r.Var<string>("x");
                r.Erasing(t.Inner);
                r.When(t.Fact.In(t.Inner).Of(x));
                r.Then(t.Seen.Of(x));
            });
            Assert.Same(t.Inner, rule.Licence);
            var plain = t.Rules.DefineRule("plain", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            Assert.Null(plain.Licence);

            // A rule's own licence wins over its ruleset's.
            var own = licensed.DefineRule("own licence", r =>
            {
                var x = r.Var<string>("x");
                r.Erasing(t.Inner);
                r.When(t.Fact.In(t.Inner).Of(x));
                r.Then(t.Seen.Of(x));
            });
            Assert.Same(t.Inner, own.Licence);
        }

        [Fact]
        public void RulesReadingOnlyTermAndAnalysisTablesAreUnaffected()
        {
            var (_, licensed, term, analysis) = Theory();
            var rule = licensed.DefineRule("mentions", r =>
            {
                var (p, n) = r.Vars<string, Node>("p", "n");
                r.When(n.Is(term.Of(p)), analysis.Of(p));
                r.Then(n.Is(term.Of("seen")));
            });
            Assert.Empty(rule.ReadScopes);
            Assert.NotNull(rule.Licence);
        }

        [Fact]
        public void AnErasingRuleJudgesOnTheLicenceScopeOnly()
        {
            // A conjunct never proves itself redundant: its own consequence in the query never deletes it; only the
            // outer scope's fact does.
            var (t, licensed, term, _) = Theory();
            licensed.DefineRule("declared", r =>
            {
                var (p, n) = r.Vars<string, Node>("p", "n");
                r.When(n.Is(term.Of(p)), t.Fact.In(t.Outer).Of(p));
                r.Then(n.Is(term.Of("true")));
            });
            t.Root.Freeze();
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("declared"));
            outer.Freeze();
            var query = outer.CreateChild(t.Inner);
            var declared = query.Make(term.Of("declared"));
            var own = query.Make(term.Of("own"));
            query.Assume(t.Fact.Of("own"));

            query.Run(Schedule.Saturate(licensed));

            Assert.True(query.AreEqual(declared, term.Of("true")));
            Assert.False(query.AreEqual(own, term.Of("true")));
        }

    }

}
