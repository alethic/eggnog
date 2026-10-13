using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    public sealed partial class Scenario04UndiscriminatedAnyOfTests
    {

        /// <summary>
        /// With the schema's split on the hidden discriminator, the two branches meet: the case join of
        /// <c>dom(status)</c> is their union, so the schema declares the union of the branch domains, and neither
        /// branch's own field is lifted. A query that compares status with a value outside the union is refuted; it
        /// mentions <c>status</c>, not <c>$anyOf/0</c>, so it opens no case of its own: a query splits only the
        /// discriminators it mentions.
        /// </summary>
        [Fact]
        public void AnUndiscriminatedUnionDeclaresTheUnionOfItsDomains()
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild(c.Schema);
            DeclareContainer(c, schema);

            var compiled = schema.Run(c.Facts);
            Assert.True(compiled.Saturated);
            Assert.Equal(2, compiled.CasesOpened);                  // $anyOf/0 = 0, $anyOf/0 = 1
            Assert.Empty(schema.Children);
            Assert.Equal(ValueSet.Of("active", "archived", "draft"), schema.Get(c.Dom.Of("status")));
            Assert.False(schema.Check(c.Present.Of("legacyId")));     // only the first branch declares it
            Assert.False(schema.Check(c.Present.Of("id2")));          // only the second
            schema.Freeze();

            using var query = schema.CreateChild(c.Query);
            var q = query.Make(c.StrEq.Of("status", "purged"));
            query.Assume(c.WhereClause.Of(q));
            var report = query.Run(c.Saturation);

            Assert.True(report.Saturated);
            Assert.Equal(0, report.CasesOpened);                    // status is mentioned, $anyOf/0 is not
            Assert.Empty(query.Children);
            Assert.True(query.IsContradictory);
            Assert.True(query.AreEqual(q, c.PFalse.Of()));
            var best = query.Extract(q);
            Assert.Equal("(PFalse)", best.ToString());
            Assert.Equal(0, best.Cost);
        }

    }

}
