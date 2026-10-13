using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// prototypes/cosmos-facts/02-query-disjunction.egg: kind IN ('order', 'invoice') lowers the UUID comparison on ref
    /// only through a case split, and the schema alone still proves nothing about ref, because a note carries none.
    /// </summary>
    public sealed partial class Scenario02QueryDisjunctionTests
    {

        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        /// <summary>
        /// The container: kind is required and one of order, invoice, note; order and invoice declare ref a canonical
        /// lowercase UUID.
        /// </summary>
        static void DeclareContainer(CosmosTheory c, Scope schema)
        {
            schema.Assume(
                c.Present.Of("kind"),
                c.Dom.Of("kind").Is(ValueSet.Of("order", "invoice", "note")),
                c.Discriminator.Of("kind"));
            c.DeclareBranch(schema, "kind", "order", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(schema, "kind", "invoice", c.Form.Of("ref", "uuid-lower"));
        }

        /// <summary>
        /// Compiles the container with horn and lattice rules only, without a split.
        /// </summary>
        static (CosmosTheory C, Scope Schema) CompileContainerWithoutSplit()
        {
            var graph = new EGraph();                               // serial by default: deterministic
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();                                    // empty and frozen: containers are siblings beneath it

            var schema = graph.Root.CreateChild(c.Schema);
            DeclareContainer(c, schema);
            var compiled = schema.Run(Schedule.Saturate(c.Horn, c.Lattice));
            Assert.True(compiled.Saturated);
            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
            schema.Freeze();
            return (c, schema);
        }

        /// <summary>
        /// (let $q (And (Or (StrEq "kind" "order") (StrEq "kind" "invoice")) (UuidEq "ref" ...))) (where $q)
        /// </summary>
        static Pred AssertQuery(CosmosTheory c, Scope query)
        {
            var q = query.Make(c.And.Of(
                c.Or.Of(c.StrEq.Of("kind", "order"), c.StrEq.Of("kind", "invoice")),
                c.UuidEq.Of("ref", Ref)));
            query.Assume(c.WhereClause.Of(q));
            return q;
        }

        static Term<Pred> Lowered(CosmosTheory c) => c.And.Of(
            c.Or.Of(c.StrEq.Of("kind", "order"), c.StrEq.Of("kind", "invoice")),
            c.StrEq.Of("ref", Ref));

        [Fact]
        public void HornAndLatticesAloneDoNotLower()
        {
            var (c, schema) = CompileContainerWithoutSplit();
            using var query = schema.CreateChild(c.Query);
            var q = AssertQuery(c, query);

            // (run-schedule (saturate horn lattice rewrite))
            var report = query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.Empty(query.Children);
            Assert.Equal(ValueSet.Of("invoice", "order"), query.Get(c.Dom.Of("kind")));   // the domain narrows...
            Assert.False(query.Check(c.Form.Of("ref", "uuid-lower")));                     // ...but no single fact proves a guard
            Assert.False(query.AreEqual(q, Lowered(c)));
        }

    }

}
