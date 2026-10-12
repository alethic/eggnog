using Xunit;

namespace Eggnog.Tests.Cosmos.Literal
{

    /// <summary>
    /// <c>prototypes/cosmos-facts/03-combining-claims.egg</c>, as written (gap 2): the schema's
    /// <c>kind ∈ {order, invoice}</c> and the query's <c>kind &lt;&gt; 'invoice'</c> only together prove the guard
    /// <c>kind = 'order'</c>; the domain lattice narrows to <c>{order}</c>. The expected figures are the egglog CLI's
    /// (vendored <c>ext/egglog</c> at 5e655cae).
    /// </summary>
    public sealed class Literal03CombiningClaimsTests
    {

        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        [Fact]
        public void ADomainLessARefusedValueProvesTheGuard()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var root = c.Graph.Root;
            root.Assume(
                c.Present.Of(c.Schema.Of(), "kind"),
                c.Dom.Of(c.Schema.Of(), "kind").Is(ValueSet.Of("order", "invoice")));
            c.DeclareFormUnder("kind", "order", "ref", "uuid-lower");
            var q = root.Make(c.And.Of(c.Not.Of(c.StrEq.Of("kind", "invoice")), c.UuidEq.Of("ref", Ref)));
            root.Assume(c.WhereClause.Of(q));

            // Horn alone: the guard never fires.
            root.Run(Schedule.Saturate(c.Horn, c.Rewrite));
            Assert.False(root.Check(c.Form.Of(c.Query.Of(), "ref", "uuid-lower")));

            // With the lattice: the domain narrows to {order}, the guard fires, the comparison lowers.
            var report = root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            Assert.True(report.Saturated);
            Assert.Equal(ValueSet.Of("order"), root.Get(c.Dom.Of(c.Query.Of(), "kind")));
            Assert.True(root.Check(c.Dom.Of(c.Query.Of(), "kind").Is(ValueSet.Of("order"))));
            Assert.True(root.Check(c.Form.Of(c.Query.Of(), "ref", "uuid-lower")));
            Assert.True(root.AreEqual(q, c.And.Of(c.Not.Of(c.StrEq.Of("kind", "invoice")), c.StrEq.Of("ref", Ref))));

            var best = root.Extract(q);
            Assert.Equal($"(And (Not (StrEq \"kind\" \"invoice\")) (StrEq \"ref\" \"{Ref}\"))", best.ToString());
            Assert.Equal(8, best.Cost);
        }

    }

}
