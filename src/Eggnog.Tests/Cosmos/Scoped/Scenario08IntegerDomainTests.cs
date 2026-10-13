using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// Gap 5 (D37), which the prototype lacks: an integer enum against a bound. The schema declares n present with the
    /// integer domain <c>idom</c>; a query's bounds narrow the domain in the query scope, and an empty domain contradicts.
    /// </summary>
    public sealed class Scenario08IntegerDomainTests
    {

        /// <summary>
        /// A container where n is present and one of <paramref name="values"/>.
        /// </summary>
        static (CosmosTheory C, Scope Schema) CompileContainer(params long[] values)
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild(c.Schema);
            schema.Assume(c.Present.Of("n"), c.IDom.Of("n").Is(ValueSet.Of(values)));
            Assert.True(schema.Run(Schedule.Saturate(c.Horn, c.Lattice)).Saturated);
            schema.Freeze();
            return (c, schema);
        }

        /// <summary>
        /// n &gt; 499 AND n &lt; 501, asked in a new query scope.
        /// </summary>
        static (Scope Query, Pred Q) AskFiveHundred(CosmosTheory c, Scope schema)
        {
            var query = schema.CreateChild(c.Query);
            var q = query.Make(c.And.Of(c.NumGt.Of("n", 499L), c.NumLt.Of("n", 501L)));
            query.Assume(c.WhereClause.Of(q));
            Assert.True(query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite)).Saturated);
            Assert.Empty(query.Children);
            return (query, q);
        }

        [Fact]
        public void AnIntegerOutsideADeclaredDomainIsFalse()
        {
            var (c, schema) = CompileContainer(100L, 200L, 300L);
            var (query, q) = AskFiveHundred(c, schema);
            using (query)
            {
                Assert.True(query.IsContradictory);
                Assert.True(query.AreEqual(q, c.PFalse.Of()));
                Assert.False(schema.IsContradictory);
                Assert.Equal("(PFalse)", query.Extract(q).ToString());
            }
        }

        [Fact]
        public void AnIntegerInsideTheDomainIsLeftAlone()
        {
            var (c, schema) = CompileContainer(100L, 500L);
            var (query, q) = AskFiveHundred(c, schema);
            using (query)
            {
                Assert.False(query.IsContradictory);
                Assert.Equal(ValueSet.Of(500L), query.Get(c.IDom.Of("n")));
                Assert.Equal(ValueSet.Of(100L, 500L), schema.Get(c.IDom.Of("n")));
                Assert.False(query.AreEqual(q, c.PFalse.Of()));
            }
        }

    }

}
