using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// prototypes/cosmos-facts/07-sweep-links.egg with scopes as contexts: sweep's <c>links</c> container and the queries
    /// its facade probe asks, the ones that need no case split (R2). Each query runs in its own child of the frozen
    /// schema; one schema serves them all.
    /// </summary>
    public sealed partial class Scenario07SweepLinksTests
    {

        /// <summary>
        /// A graph with the theory, an empty frozen root, and the compiled container.
        /// </summary>
        static (CosmosTheory C, Scope Schema) Container()
        {
            var c = new CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            return (c, SweepLinks.Compile(c));
        }

        [Fact]
        public void TheMapQueryLowers()
        {
            var (c, schema) = Container();
            var (query, q) = SweepLinks.Ask(c, schema, c.And.Of(c.StrEq.Of("data.type", "map"), c.UuidEq.Of("data.data.mapId", SweepLinks.Map)));
            using (query)
            {
                Assert.True(query.AreEqual(q, c.And.Of(c.StrEq.Of("data.type", "map"), c.StrEq.Of("data.data.mapId", SweepLinks.Map))));
                Assert.True(query.Check(c.Form.Of("data.data.mapId", "uuid-lower")));
                Assert.False(schema.Check(c.Form.Of("data.data.mapId", "uuid-lower")));
                var best = query.Extract(q);
                Assert.Equal($"(And (StrEq \"data.type\" \"map\") (StrEq \"data.data.mapId\" \"{SweepLinks.Map}\"))", best.ToString());
                Assert.Equal(7, best.Cost);
            }
        }

        [Fact]
        public void TheFacadeFilterDropsTheWholeEnum()
        {
            var (c, schema) = Container();
            var (query, q) = SweepLinks.Ask(c, schema, c.And.Of(
                c.StrEq.Of("type", "Link"),
                c.Or.Of(c.StrEq.Of("data.type", "park"), c.Or.Of(c.StrEq.Of("data.type", "map"), c.StrEq.Of("data.type", "spot")))));
            using (query)
            {
                Assert.True(query.AreEqual(q, c.StrEq.Of("type", "Link")));
                var best = query.Extract(q);
                Assert.Equal("(StrEq \"type\" \"Link\")", best.ToString());
                Assert.Equal(3, best.Cost);
            }
        }

        [Fact]
        public void ASpotMayBeAmongTheRowsSoNothingLowers()
        {
            var (c, schema) = Container();
            var (query, q) = SweepLinks.Ask(c, schema, c.And.Of(c.Not.Of(c.StrEq.Of("data.type", "map")), c.UuidEq.Of("data.data.parkId", SweepLinks.Park)));
            using (query)
            {
                Assert.False(query.Check(c.Form.Of("data.data.parkId", "uuid-lower")));
                Assert.Equal(ValueSet.Of("park", "spot"), query.Get(c.Dom.Of("data.type")));
                var best = query.Extract(q);
                Assert.True(best.Mentions(c.UuidEq));
                Assert.Equal($"(And (Not (StrEq \"data.type\" \"map\")) (UuidEq \"data.data.parkId\" \"{SweepLinks.Park}\"))", best.ToString());
                Assert.Equal(1007, best.Cost);
            }
        }

        [Fact]
        public void OneSchemaServesEveryQuery()
        {
            var (c, schema) = Container();
            var (map, mapQ) = SweepLinks.Ask(c, schema, c.And.Of(c.StrEq.Of("data.type", "map"), c.UuidEq.Of("data.data.mapId", SweepLinks.Map)));
            var (spot, spotQ) = SweepLinks.Ask(c, schema, c.And.Of(c.Not.Of(c.StrEq.Of("data.type", "map")), c.UuidEq.Of("data.data.parkId", SweepLinks.Park)));
            using (map)
            using (spot)
            {
                Assert.Equal(2, schema.Children.Count);
                Assert.False(map.Extract(mapQ).Mentions(c.UuidEq));
                Assert.True(spot.Extract(spotQ).Mentions(c.UuidEq));
                Assert.False(spot.Check(c.Form.Of("data.data.mapId", "uuid-lower")));
            }

            Assert.Empty(schema.Children);
        }

    }

}
