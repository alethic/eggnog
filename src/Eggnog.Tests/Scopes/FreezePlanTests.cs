using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// The plans a freeze makes (a rule is planned once, and every later run reuses the plan): the default variants of
    /// every rule a descendant can run, planned on the frozen scope's statistics: every global rule, the scope's local
    /// rules, every query and every mirror copy rule; another container's local rules are left to be planned on their
    /// own statistics.
    /// </summary>
    public sealed class FreezePlanTests
    {

        [Fact]
        public void AFreezePlansEveryRuleADescendantCanRunAndNoOtherContainersLocalRules()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            var global = t.DefinePaths();
            var innerReader = t.Rules.DefineRule("inner reader", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.In(t.Inner).Of(x));
                r.Then(t.Seen.Of(x));
            });
            var query = t.Graph.DefineQuery("seen", q => q.When(t.Seen.Of(q.Var<string>("x"))));
            t.Root.Freeze();

            // An untagged scope, so that its freeze syncs no mirror and runs nothing: whatever is planned, the freeze
            // planned.
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
            var copyRules = t.Graph.Mirrors.AllCopyRules();
            Assert.Equal(3, copyRules.Length);

            var planned = new[]
            {
                global.Compiled.Variants.Default!, copies.FactRule.Compiled.Variants.Default!, innerReader.Compiled.Variants.Default!,
                own.Compiled.Variants.Default!, query.Compiled.Variants.Default!,
                copyRules[0].Variants.Default!, copyRules[1].Variants.Default!, copyRules[2].Variants.Default!,
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

        /// <summary>
        /// A split matcher is a rule a descendant can run (a split step in a query beneath the frozen schema), so the
        /// schema's freeze plans it too.
        /// </summary>
        [Fact]
        public void AFreezePlansTheSplitMatchers()
        {
            var t = new ScopeTheory();
            var dom = t.Graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var split = t.Graph.DefineDomainSplit("facts", dom, [t.Fact]);
            t.Root.Freeze();
            var matcher = split.Program.Variants.Default!;
            Assert.StartsWith("$split/facts#", matcher.Description, System.StringComparison.Ordinal);
            Assert.Null(t.Graph.Engine.GetRuleInfo(matcher.Id).CachedPlan);

            t.Root.CreateChild().Freeze();

            Assert.NotNull(t.Graph.Engine.GetRuleInfo(matcher.Id).CachedPlan);
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
