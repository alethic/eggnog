using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// prototypes/cosmos-facts/05-intervals.egg with scopes as contexts: the schema bounds n to [0, 100]; a
    /// query beyond the bound contradicts the schema in the query scope, and the predicate becomes false; one inside
    /// the range is left alone.
    /// </summary>
    public sealed class Scenario05IntervalsTests
    {

        /// <summary>
        /// The container: n is a number in [0, 100].
        /// </summary>
        static (CosmosTheory C, Scope Schema) CompileContainer()
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild(c.Schema);
            schema.Assume(c.Types.Of("n").Is(ValueSet.Of("number")), c.Lo.Of("n").Is(0L), c.Hi.Of("n").Is(100L));
            Assert.True(schema.Run(Schedule.Saturate(c.Horn, c.Lattice)).Saturated);
            schema.Freeze();
            return (c, schema);
        }

        [Fact]
        public void AComparisonBeyondADeclaredBoundIsFalse()
        {
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(c.Query);
            var q = query.Make(c.NumGt.Of("n", 500L));
            query.Assume(c.WhereClause.Of(q));

            var report = query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.True(query.IsContradictory);
            Assert.False(schema.IsContradictory);
            Assert.True(query.AreEqual(q, c.PFalse.Of()));
            Assert.Equal(501L, query.Get(c.Lo.Of("n")));
            Assert.Equal(0L, schema.Get(c.Lo.Of("n")));
            Assert.Empty(query.Children);
            var best = query.Extract(q);
            Assert.Equal("(PFalse)", best.ToString());
            Assert.Equal(0, best.Cost);
        }

        [Fact]
        public void ABoundInsideTheRangeIsLeftAlone()
        {
            var (c, schema) = CompileContainer();
            using var query = schema.CreateChild(c.Query);
            var q = query.Make(c.NumGt.Of("n", 50L));
            query.Assume(c.WhereClause.Of(q));

            var report = query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.False(query.IsContradictory);
            Assert.Equal(51L, query.Get(c.Lo.Of("n")));
            Assert.True(query.Check(c.Lo.Of("n").Is(51L)));
            Assert.Equal(100L, query.Get(c.Hi.Of("n")));
            Assert.Empty(query.Children);
            var best = query.Extract(q);
            Assert.Equal("(NumGt \"n\" 50)", best.ToString());
            Assert.Equal(3, best.Cost);
        }

    }

}
