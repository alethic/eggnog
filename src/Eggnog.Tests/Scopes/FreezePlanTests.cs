using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// The plans a freeze makes (a rule is planned once, and every later run reuses the plan): the default variants of
    /// every rule a descendant can run, planned on the frozen scope's statistics: every global rule, the scope's local
    /// rules and every query; a sibling scope's local rules are left to be planned on their own statistics.
    /// </summary>
    public sealed class FreezePlanTests
    {

        [Fact]
        public void AFreezePlansEveryRuleADescendantCanRunAndNoSiblingsLocalRules()
        {
            var t = new ScopeTheory();
            var global = t.DefinePaths();
            var reader = t.Rules.DefineRule("reader", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            var query = t.Graph.DefineQuery("seen", q => q.When(t.Seen.Of(q.Var<string>("x"))));
            t.Root.Freeze();

            // The scope's freeze runs nothing: whatever is planned, the freeze planned.
            var schema = t.Root.CreateChild();
            var sibling = t.Root.CreateChild();
            var own = schema.DefineRule(t.Rules, "own", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Fact.Of("own"));
            });
            var other = sibling.DefineRule(t.Rules, "other", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Fact.Of("other"));
            });
            var planned = new[]
            {
                global.Compiled.Variants.Default!, reader.Compiled.Variants.Default!, own.Compiled.Variants.Default!,
                query.Compiled.Variants.Default!,
            };
            var unplanned = other.Compiled.Variants.Default!;
            foreach (var variant in planned)
                Assert.Null(t.Graph.Engine.GetRuleInfo(variant.Id).CachedPlan);

            Assert.Null(t.Graph.Engine.GetRuleInfo(unplanned.Id).CachedPlan);

            schema.Freeze();

            foreach (var variant in planned)
                Assert.True(t.Graph.Engine.GetRuleInfo(variant.Id).CachedPlan is not null, variant.Description);

            Assert.Null(t.Graph.Engine.GetRuleInfo(unplanned.Id).CachedPlan);
        }

        [Fact]
        public void TheRootsFreezePlansNothing()
        {
            var t = new ScopeTheory();
            var global = t.DefinePaths();
            t.Root.Freeze();

            Assert.Null(t.Graph.Engine.GetRuleInfo(global.Compiled.Variants.Default!.Id).CachedPlan);
        }

    }

}
