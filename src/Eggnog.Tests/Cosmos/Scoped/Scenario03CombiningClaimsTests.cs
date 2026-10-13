using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// prototypes/cosmos-facts/03-combining-claims.egg with scopes as contexts (gap 2): the schema says kind is one of
    /// order and invoice, the query says it is not invoice, and only together, through the domain lattice in the query,
    /// do they prove the guard kind = 'order'.
    /// </summary>
    public sealed class Scenario03CombiningClaimsTests
    {

        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        [Fact]
        public void ADomainLessARefusedValueProvesTheGuard()
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild(c.Schema);
            schema.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("order", "invoice")));
            c.DeclareBranch(schema, "kind", "order", c.Form.Of("ref", "uuid-lower"));
            Assert.True(schema.Run(Schedule.Saturate(c.Horn, c.Lattice)).Saturated);
            schema.Freeze();

            using var query = schema.CreateChild(c.Query);
            var q = query.Make(c.And.Of(c.Not.Of(c.StrEq.Of("kind", "invoice")), c.UuidEq.Of("ref", Ref)));
            query.Assume(c.WhereClause.Of(q));

            // Horn alone: the guard never fires.
            Assert.True(query.Run(Schedule.Saturate(c.Horn, c.Rewrite)).Saturated);
            Assert.Empty(query.Children);
            Assert.False(query.Check(c.Form.Of("ref", "uuid-lower")));

            // With the lattice: the domain narrows to {order} in the query, the guard fires, the comparison lowers.
            Assert.True(query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite)).Saturated);
            Assert.Equal(ValueSet.Of("order"), query.Get(c.Dom.Of("kind")));
            Assert.True(query.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.True(query.AreEqual(q, c.And.Of(c.Not.Of(c.StrEq.Of("kind", "invoice")), c.StrEq.Of("ref", Ref))));
            Assert.Equal(ValueSet.Of("invoice", "order"), schema.Get(c.Dom.Of("kind")));
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.Empty(query.Children);
            var best = query.Extract(q);
            Assert.False(best.Mentions(c.UuidEq));
            Assert.Equal($"(And (Not (StrEq \"kind\" \"invoice\")) (StrEq \"ref\" \"{Ref}\"))", best.ToString());
        }

    }

}
