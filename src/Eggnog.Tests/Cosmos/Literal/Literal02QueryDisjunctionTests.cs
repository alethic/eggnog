using Xunit;

namespace Eggnog.Tests.Cosmos.Literal
{

    /// <summary>
    /// <c>prototypes/cosmos-facts/02-query-disjunction.egg</c>, as written: <c>kind IN ('order', 'invoice')</c>
    /// lowers the UUID comparison on <c>ref</c> only through the prototype's case split, written as rules, and the
    /// schema alone still proves nothing about <c>ref</c>, because a note carries none. The expected figures are the
    /// egglog CLI's (vendored <c>ext/egglog</c> at 5e655cae).
    /// </summary>
    public sealed class Literal02QueryDisjunctionTests
    {

        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        [Fact]
        public void TwoKindsNamedTogetherLowerTheReference()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var root = c.Graph.Root;
            root.Assume(
                c.Present.Of(c.Schema.Of(), "kind"),
                c.Dom.Of(c.Schema.Of(), "kind").Is(ValueSet.Of("order", "invoice", "note")),
                c.Discriminator.Of("kind", "order"),
                c.Discriminator.Of("kind", "invoice"),
                c.Discriminator.Of("kind", "note"));
            c.DeclareFormUnder("kind", "order", "ref", "uuid-lower");
            c.DeclareFormUnder("kind", "invoice", "ref", "uuid-lower");
            var q = root.Make(c.And.Of(c.Or.Of(c.StrEq.Of("kind", "order"), c.StrEq.Of("kind", "invoice")), c.UuidEq.Of("ref", Ref)));
            root.Assume(c.WhereClause.Of(q));

            // Horn and lattices alone: nothing fires.
            var first = root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            Assert.True(first.Saturated);
            Assert.False(root.Check(c.Form.Of(c.Query.Of(), "ref", "uuid-lower")));

            // With the split: each case proves the form, and together they cover the query.
            var second = root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));
            Assert.True(second.Saturated);
            Assert.True(root.Check(c.Form.Of(c.Query.Of(), "ref", "uuid-lower")));
            Assert.True(root.AreEqual(q, c.And.Of(c.Or.Of(c.StrEq.Of("kind", "order"), c.StrEq.Of("kind", "invoice")), c.StrEq.Of("ref", Ref))));

            // And the schema alone still proves nothing about ref: a note carries none.
            Assert.False(root.Check(c.Form.Of(c.Schema.Of(), "ref", "uuid-lower")));

            var best = root.Extract(q);
            Assert.Equal(11, best.Cost);
            Assert.Equal($"(And (Or (StrEq \"kind\" \"order\") (StrEq \"kind\" \"invoice\")) (StrEq \"ref\" \"{Ref}\"))", best.ToString());
        }

    }

}
