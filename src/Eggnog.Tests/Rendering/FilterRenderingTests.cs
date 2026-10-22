using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rendering
{

    /// <summary>
    /// A filter renders as egglog writes one: a comparison or a set membership as egglog's fallible primitive
    /// (<c>&lt;</c>, <c>set-contains</c>, <c>!=</c>), which matches where it has a result, and any other condition as
    /// <c>(guard …)</c>; the same comparison as a value renders as egglog's <c>bool-&lt;</c>.
    /// </summary>
    public sealed class FilterRenderingTests
    {

        [Fact]
        public void AComparisonFiltersAsEgglogsFallibleFormAndIsAValueAsItsBoolForm()
        {
            var t = new NodeTheory();
            var flag = t.Graph.DefineRelation<bool>("flag");
            var rule = t.Rules.DefineRule("small", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 5L));
                r.Then(flag.Of(Primitives.GreaterOrEqual.Of(x, 3L)));
            });

            Assert.Equal("(rule ((num x) (< x 5)) ((flag (bool->= x 3))) :ruleset rules)", rule.ToString());
        }

        [Fact]
        public void ASetMembershipAndADisequalityFilterAsEgglogsFallibleForms()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            var pick = graph.DefineRelation<string, string>("pick");
            var rules = graph.DefineRuleset("rules");
            var rule = rules.DefineRule("pick", r =>
            {
                var (p, d, v) = r.Vars<string, Set<string>, string>("p", "d", "v");
                r.When(d.Is(dom.Of(p)), pick.Of(p, v));
                r.Where(Primitives.Contains<string>().Of(d, v));
                r.Where(Primitives.NotContains<string>().Of(d, p));
                r.Where(Primitives.NotEqual<string>().Of(p, v));
                r.Then(pick.Of(v, p));
            });

            Assert.Equal(
                "(rule ((= d (dom p)) (pick p v) (set-contains d v) (set-not-contains d p) (!= p v)) ((pick v p)) :ruleset rules)",
                rule.ToString());
        }

        [Fact]
        public void AnyOtherConditionFiltersAsGuard()
        {
            var t = new NodeTheory();
            var flag = t.Graph.DefineRelation<bool>("flag");
            var rule = t.Rules.DefineRule("guards", r =>
            {
                var (x, b) = r.Vars<long, bool>("x", "b");
                r.When(t.Num.Of(x), flag.Of(b));
                r.Where(b);
                r.Where(BooleanPrimitives.And.Of(b, Primitives.Equal.Of(x, 1L)));
                r.Then(t.Num.Of(x));
            });

            Assert.Equal("(rule ((num x) (flag b) (guard b) (guard (and b (bool-= x 1)))) ((num x)) :ruleset rules)", rule.ToString());
        }

    }

}
