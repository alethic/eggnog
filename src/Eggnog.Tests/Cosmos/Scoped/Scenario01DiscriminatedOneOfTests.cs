using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// prototypes/cosmos-facts/01-discriminated-oneof.egg with scopes as contexts: the "B" branch of a discriminated
    /// <c>oneOf</c> is a rule local to the container's schema scope, so <c>kind = 'B'</c> in a query lowers the UUID
    /// comparison, and without the discriminator nothing lowers.
    /// </summary>
    public sealed class Scenario01DiscriminatedOneOfTests
    {

        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        /// <summary>
        /// The container: nothing assumed, one branch rule; compiled and frozen under an empty frozen root.
        /// </summary>
        static (CosmosTheory C, Scope Schema) CompileContainer()
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild(c.Schema);
            c.DeclareBranch(schema, "kind", "B", c.Form.Of("ref", "uuid-lower"));
            Assert.True(schema.Run(Schedule.Saturate(c.Horn, c.Lattice)).Saturated);
            schema.Freeze();
            return (c, schema);
        }

        [Fact]
        public void ADeclaredStoredFormPutsTheComparisonInTheStatement()
        {
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(c.Query);
            var q = query.Make(c.And.Of(c.StrEq.Of("kind", "B"), c.UuidEq.Of("ref", Ref)));
            query.Assume(c.WhereClause.Of(q));

            var report = query.Run(Schedule.Saturate(c.Horn, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.Empty(query.Children);
            Assert.True(query.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
            Assert.True(query.AreEqual(q, c.And.Of(c.StrEq.Of("kind", "B"), c.StrEq.Of("ref", Ref))));
            var best = query.Extract(q);
            Assert.Equal($"(And (StrEq \"kind\" \"B\") (StrEq \"ref\" \"{Ref}\"))", best.ToString());
            Assert.Equal(7, best.Cost);
        }

        [Fact]
        public void WithoutTheDiscriminatorNothingIsProvenAndNothingLowers()
        {
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(c.Query);
            var q = query.Make(c.UuidEq.Of("ref", Ref));
            query.Assume(c.WhereClause.Of(q));

            var report = query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.Empty(query.Children);
            Assert.False(query.Check(c.Form.Of("ref", "uuid-lower")));
            var best = query.Extract(q);
            Assert.Equal($"(UuidEq \"ref\" \"{Ref}\")", best.ToString());
            Assert.Equal(1002, best.Cost);
        }

    }

}
