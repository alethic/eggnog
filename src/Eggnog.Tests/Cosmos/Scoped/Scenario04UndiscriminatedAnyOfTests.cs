using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// prototypes/cosmos-facts/04-undiscriminated-anyof.egg with scopes as contexts, without the split: the
    /// <c>anyOf</c>'s branches are rules local to the schema, guarded on the hidden discriminator <c>$anyOf/0</c>, and
    /// with horn and lattice rules alone they never meet: the schema's domain of status stays open, and a query for a
    /// value outside both branches is not refuted. The schema's split unions the branch domains
    /// (<see cref="AnUndiscriminatedUnionDeclaresTheUnionOfItsDomains"/>).
    /// </summary>
    public sealed partial class Scenario04UndiscriminatedAnyOfTests
    {

        /// <summary>
        /// The container's facts and branch rules.
        /// </summary>
        static void DeclareContainer(CosmosTheory c, Scope schema)
        {
            schema.Assume(
                c.Present.Of("$anyOf/0"),
                c.Dom.Of("$anyOf/0").Is(ValueSet.Of("0", "1")),
                c.Discriminator.Of("$anyOf/0"));
            c.DeclareBranch(schema, "$anyOf/0", "0", c.Present.Of("legacyId"), c.Dom.Of("status").Is(ValueSet.Of("active", "archived")));
            c.DeclareBranch(schema, "$anyOf/0", "1", c.Present.Of("id2"), c.Dom.Of("status").Is(ValueSet.Of("draft", "active")));
        }

        [Fact]
        public void WithoutTheSplitTheBranchesNeverMeet()
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild(c.Schema);
            DeclareContainer(c, schema);
            Assert.True(schema.Run(Schedule.Saturate(c.Horn, c.Lattice)).Saturated);
            schema.Freeze();

            using var query = schema.CreateChild(c.Query);
            var q = query.Make(c.StrEq.Of("status", "purged"));
            query.Assume(c.WhereClause.Of(q));
            var report = query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.False(schema.TryGet(c.Dom.Of("status"), out _));
            Assert.False(schema.Check(c.Dom.Of("status").Is(ValueSet.Of("active", "archived", "draft"))));
            Assert.False(query.IsContradictory);
            Assert.False(query.AreEqual(q, c.PFalse.Of()));
            Assert.False(schema.Check(c.Present.Of("legacyId")));
            Assert.False(schema.Check(c.Present.Of("id2")));
            Assert.Empty(query.Children);
            Assert.Equal("(StrEq \"status\" \"purged\")", query.Extract(q).ToString());
        }

    }

}
