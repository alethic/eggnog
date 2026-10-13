using System;

using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// prototypes/cosmos-facts/06-guard-deletion.egg with scopes as contexts: guard deletion and tautologies as rewrites
    /// plus extraction. Deletions read facts only from the schema (the rewrite ruleset is erasing under the schema tag,
    /// D12), so a conjunct's own consequence in the query never deletes it.
    /// </summary>
    public sealed class Scenario06GuardDeletionTests
    {

        const string At = "2024-01-01T00:00:00Z";

        /// <summary>
        /// A container with the given schema facts, compiled and frozen under an empty frozen root.
        /// </summary>
        static (CosmosTheory C, Scope Schema) CompileContainer(Func<CosmosTheory, Fact[]> facts)
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild(c.Schema);
            var declared = facts(c);
            if (declared.Length > 0)
                schema.Assume(declared);

            Assert.True(schema.Run(Schedule.Saturate(c.Horn, c.Lattice)).Saturated);
            schema.Freeze();
            return (c, schema);
        }

        /// <summary>
        /// Asks a query in its own child of the schema and saturates it.
        /// </summary>
        static (Scope Query, Pred Q) Ask(CosmosTheory c, Scope schema, ConstructorTerm<Pred> term)
        {
            var query = schema.CreateChild(c.Query);
            var q = query.Make(term);
            query.Assume(c.WhereClause.Of(q));
            Assert.True(query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite)).Saturated);
            Assert.Empty(query.Children);
            return (query, q);
        }

        /// <summary>
        /// IS_DEFINED(at) AND (NOT IS_STRING(at) OR at &gt;= v): the weakened ordering comparison.
        /// </summary>
        static ConstructorTerm<Pred> Weakened(CosmosTheory c) =>
            c.And.Of(c.IsDefined.Of("at"), c.Or.Of(c.Not.Of(c.IsString.Of("at")), c.StrGe.Of("at", At)));

        [Fact]
        public void ADeclaredTypeMakesAnOrderingComparisonExact()
        {
            var (c, schema) = CompileContainer(c => [c.Types.Of("at").Is(ValueSet.Of("string"))]);
            var (query, q) = Ask(c, schema, Weakened(c));
            using (query)
            {
                Assert.True(query.AreEqual(q, c.StrGe.Of("at", At)));
                var best = query.Extract(q);
                Assert.Equal($"(StrGe \"at\" \"{At}\")", best.ToString());
                Assert.Equal(3, best.Cost);
            }
        }

        [Fact]
        public void WithoutADeclaredTypeTheComparisonIsStillWeakened()
        {
            var (c, schema) = CompileContainer(_ => []);
            var (query, q) = Ask(c, schema, Weakened(c));
            using (query)
            {
                Assert.False(query.AreEqual(q, c.StrGe.Of("at", At)));
                var best = query.Extract(q);
                Assert.Equal($"(And (IsDefined \"at\") (Or (Not (IsString \"at\")) (StrGe \"at\" \"{At}\")))", best.ToString());
                Assert.Equal(10, best.Cost);
            }
        }

        [Fact]
        public void ADeclaredTautologyIsNotAsked()
        {
            var (c, schema) = CompileContainer(c => [c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("A"))]);
            var (query, q) = Ask(c, schema, c.And.Of(c.StrEq.Of("kind", "A"), c.StrEq.Of("id", "x")));
            using (query)
            {
                Assert.True(query.AreEqual(q, c.StrEq.Of("id", "x")));
                var best = query.Extract(q);
                Assert.Equal("(StrEq \"id\" \"x\")", best.ToString());
                Assert.Equal(3, best.Cost);
            }
        }

        [Fact]
        public void WithoutAPresenceClaimTheComparisonStays()
        {
            var (c, schema) = CompileContainer(c => [c.Dom.Of("kind").Is(ValueSet.Of("A"))]);
            var (query, q) = Ask(c, schema, c.And.Of(c.StrEq.Of("kind", "A"), c.StrEq.Of("id", "x")));
            using (query)
            {
                // The conjunct's own consequence holds in the query, but the deletion judges on the schema (D12).
                Assert.True(query.Check(c.Present.Of("kind")));
                Assert.False(schema.Check(c.Present.Of("kind")));
                Assert.False(query.Check(c.Present.In(c.Schema).Of("kind")));
                Assert.False(query.AreEqual(q, c.StrEq.Of("id", "x")));
                var best = query.Extract(q);
                Assert.Equal("(And (StrEq \"kind\" \"A\") (StrEq \"id\" \"x\"))", best.ToString());
                Assert.Equal(7, best.Cost);
            }
        }

    }

}
