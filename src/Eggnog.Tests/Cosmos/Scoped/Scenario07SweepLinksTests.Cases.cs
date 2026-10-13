using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// The facade probe's queries of 07 that lower only through a case split on <c>data.type</c> (R3): each runs the
    /// theory's <see cref="CosmosTheory.Saturation"/> in its own query scope under the frozen schema, which
    /// <see cref="SweepLinks.Compile"/> compiled with its own split (three cases: park, map, spot).
    /// </summary>
    public sealed partial class Scenario07SweepLinksTests
    {

        /// <summary>
        /// Asks a query with the split (<see cref="CosmosTheory.Saturation"/>), and checks that the run saturated and left
        /// no case behind.
        /// </summary>
        static (Scope Query, Pred Q, RunReport Report) AskWithSplit(CosmosTheory c, Scope schema, ConstructorTerm<Pred> term)
        {
            var query = schema.CreateChild(c.Query);
            var q = query.Make(term);
            query.Assume(c.WhereClause.Of(q));
            var report = query.Run(c.Saturation);
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Saturated);
            Assert.Empty(query.Children);
            return (query, q, report);
        }

        [Fact]
        public void ParkAndMapWrittenInLowerTheParkId()
        {
            var (c, schema) = Container();
            var (query, q, report) = AskWithSplit(c, schema,
                c.And.Of(c.Or.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.type", "map")), c.UuidEq.Of("data.data.parkId", SweepLinks.Park)));
            using (query)
            {
                Assert.Equal(2, report.CasesOpened);                // park and map: spot is excluded before the split
                Assert.True(query.Check(c.Form.Of("data.data.parkId", "uuid-lower")));
                Assert.False(schema.Check(c.Form.Of("data.data.parkId", "uuid-lower")));
                Assert.True(query.AreEqual(q, c.And.Of(
                    c.Or.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.type", "map")),
                    c.StrEq.Of("data.data.parkId", SweepLinks.Park))));
                var best = query.Extract(q);
                Assert.Equal(
                    $"(And (Or (StrEq \"data.type\" \"park\") (StrEq \"data.type\" \"map\")) (StrEq \"data.data.parkId\" \"{SweepLinks.Park}\"))",
                    best.ToString());
                Assert.Equal(11, best.Cost);
            }
        }

        [Fact]
        public void ParkAndMapSpeltPerKindLowerTheParkId()
        {
            var (c, schema) = Container();
            var (query, q, report) = AskWithSplit(c, schema, c.Or.Of(
                c.And.Of(c.StrEq.Of("data.type", "park"), c.UuidEq.Of("data.data.parkId", SweepLinks.Park)),
                c.And.Of(c.StrEq.Of("data.type", "map"), c.UuidEq.Of("data.data.parkId", SweepLinks.Park))));
            using (query)
            {
                Assert.Equal(2, report.CasesOpened);
                Assert.Equal(ValueSet.Of("map", "park"), query.Get(c.Dom.Of("data.type")));
                Assert.True(query.Check(c.Form.Of("data.data.parkId", "uuid-lower")));
                Assert.True(query.AreEqual(q, c.Or.Of(
                    c.And.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.data.parkId", SweepLinks.Park)),
                    c.And.Of(c.StrEq.Of("data.type", "map"), c.StrEq.Of("data.data.parkId", SweepLinks.Park)))));

                // Eggnog's spelling of equal cost (§8.1.1, N35): the host made the parkId comparison's class before the map
                // equality's, so the tie between the two spellings of the second conjunction goes to the commuted one (D10).
                // egglog's spelling is in the same class.
                var best = query.Extract(q);
                Assert.Equal(
                    $"(Or (And (StrEq \"data.type\" \"park\") (StrEq \"data.data.parkId\" \"{SweepLinks.Park}\")) (And (StrEq \"data.data.parkId\" \"{SweepLinks.Park}\") (StrEq \"data.type\" \"map\")))",
                    best.ToString());
                Assert.Equal(15, best.Cost);
                Assert.True(query.AreEqual(q, c.Or.Of(
                    c.And.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.data.parkId", SweepLinks.Park)),
                    c.And.Of(c.StrEq.Of("data.data.parkId", SweepLinks.Park), c.StrEq.Of("data.type", "map")))));
            }
        }

        [Fact]
        public void TheOfflineRefreshLowersEverything()
        {
            var (c, schema) = Container();
            var (query, q, report) = AskWithSplit(c, schema, OfflineRefresh(c,
                c.UuidEq.Of("data.data.parkId", SweepLinks.Park), c.InstantGt.Of("data.metadata.changeUtcTime", SweepLinks.Since)));
            using (query)
            {
                Assert.Equal(2, report.CasesOpened);
                Assert.True(query.AreEqual(q, OfflineRefresh(c,
                    c.StrEq.Of("data.data.parkId", SweepLinks.Park), c.StrGt.Of("data.metadata.changeUtcTime", SweepLinks.Since))));
                Assert.True(query.Check(c.Form.Of("data.data.parkId", "uuid-lower")));
                var best = query.Extract(q);
                Assert.False(best.Mentions(c.UuidEq));
                Assert.False(best.Mentions(c.InstantGt));
                Assert.Equal(
                    $"(And (And (Or (StrEq \"data.type\" \"park\") (StrEq \"data.type\" \"map\")) (StrEq \"data.data.parkId\" \"{SweepLinks.Park}\")) (And (BoolTrue \"data.offline\") (StrGt \"data.metadata.changeUtcTime\" \"{SweepLinks.Since}\")))",
                    best.ToString());
                Assert.Equal(18, best.Cost);
            }
        }

        /// <summary>
        /// The offline refresh's WHERE clause, with the two comparisons given.
        /// </summary>
        static ConstructorTerm<Pred> OfflineRefresh(CosmosTheory c, ConstructorTerm<Pred> park, ConstructorTerm<Pred> changed) =>
            c.And.Of(
                c.And.Of(c.Or.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.type", "map")), park),
                c.And.Of(c.BoolTrue.Of("data.offline"), changed));

    }

}
