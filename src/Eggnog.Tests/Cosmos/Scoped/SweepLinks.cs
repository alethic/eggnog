using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// Compiles sweep's <c>links</c> container (prototypes/cosmos-facts/07-sweep-links.egg) into a schema scope: what a
    /// schema compiler would emit, as facts assumed in the schema and branch rules local to it.
    /// </summary>
    public static class SweepLinks
    {

        /// <summary>
        /// A park's id.
        /// </summary>
        public const string Park = "11111111-1111-1111-1111-111111111111";

        /// <summary>
        /// A map's id.
        /// </summary>
        public const string Map = "22222222-2222-2222-2222-222222222222";

        /// <summary>
        /// The offline refresh's instant.
        /// </summary>
        public const string Since = "2026-08-02T12:00:00.000Z";

        /// <summary>
        /// Compiles the container under the graph's frozen root with horn and lattice rules (R2; R3 adds the schema's
        /// split), and freezes it.
        /// </summary>
        /// <param name="c">The theory, whose graph's root is frozen.</param>
        /// <returns>The frozen schema scope.</returns>
        public static Scope Compile(CosmosTheory c)
        {
            var schema = c.Graph.Root.CreateChild(c.Schema);
            Declare(c, schema);
            var compiled = schema.Run(Schedule.Saturate(c.Horn, c.Lattice));
            Assert.True(compiled.Saturated);
            Assert.Empty(schema.Children);
            schema.Freeze();
            return schema;
        }

        /// <summary>
        /// Declares the container's facts and branch rules in a writable schema scope.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="schema">The schema scope.</param>
        public static void Declare(CosmosTheory c, Scope schema)
        {
            schema.Assume(
                c.Present.Of("type"),
                c.Types.Of("type").Is(ValueSet.Of("string")),
                c.Present.Of("linkId"),
                c.Types.Of("linkId").Is(ValueSet.Of("number")),
                c.Lo.Of("linkId").Is(1L),
                c.Hi.Of("linkId").Is(2147483647L),
                c.Present.Of("data.type"),
                c.Dom.Of("data.type").Is(ValueSet.Of("park", "map", "spot")),
                c.Discriminator.Of("data.type"),
                c.Present.Of("data.guid"),
                c.Form.Of("data.guid", "uuid-lower"),
                c.Present.Of("data.label"),
                c.Types.Of("data.label").Is(ValueSet.Of("string")),
                c.Present.Of("data.offline"),
                c.Types.Of("data.offline").Is(ValueSet.Of("boolean")),
                c.Present.Of("data.metadata"),
                c.Form.Of("data.metadata.createUtcTime", "instant-ms"),
                c.Form.Of("data.metadata.changeUtcTime", "instant-ms"),
                c.Form.Of("data.metadata.deleteUtcTime", "instant-ms"));
            c.DeclareBranch(schema, "data.type", "park", c.Present.Of("data.data.parkId"), c.Form.Of("data.data.parkId", "uuid-lower"));
            c.DeclareBranch(schema, "data.type", "map",
                c.Present.Of("data.data.parkId"),
                c.Form.Of("data.data.parkId", "uuid-lower"),
                c.Present.Of("data.data.mapId"),
                c.Form.Of("data.data.mapId", "uuid-lower"));
            c.DeclareBranch(schema, "data.type", "spot", c.Present.Of("data.data.location"));
        }

        /// <summary>
        /// The facade probe's queries (07): the map query, park and map written IN and spelt per kind, the offline refresh,
        /// the facade's own view filter, and the spot query. Those that need a case split to lower do not lower in R2;
        /// they still exercise the schema.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <returns>The queries' WHERE clauses.</returns>
        public static ConstructorTerm<Pred>[] Queries(CosmosTheory c) =>
        [
            c.And.Of(c.StrEq.Of("data.type", "map"), c.UuidEq.Of("data.data.mapId", Map)),
            c.And.Of(c.Or.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.type", "map")), c.UuidEq.Of("data.data.parkId", Park)),
            c.Or.Of(
                c.And.Of(c.StrEq.Of("data.type", "park"), c.UuidEq.Of("data.data.parkId", Park)),
                c.And.Of(c.StrEq.Of("data.type", "map"), c.UuidEq.Of("data.data.parkId", Park))),
            c.And.Of(
                c.And.Of(c.Or.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.type", "map")), c.UuidEq.Of("data.data.parkId", Park)),
                c.And.Of(c.BoolTrue.Of("data.offline"), c.InstantGt.Of("data.metadata.changeUtcTime", Since))),
            c.And.Of(
                c.StrEq.Of("type", "Link"),
                c.Or.Of(c.StrEq.Of("data.type", "park"), c.Or.Of(c.StrEq.Of("data.type", "map"), c.StrEq.Of("data.type", "spot")))),
            c.And.Of(c.Not.Of(c.StrEq.Of("data.type", "map")), c.UuidEq.Of("data.data.parkId", Park)),
        ];

        /// <summary>
        /// Asks a query in a new child of the schema, tagged as a query, and saturates it with horn, lattice and rewrite
        /// rules (R2: no split).
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="schema">The frozen schema.</param>
        /// <param name="term">The WHERE clause.</param>
        /// <param name="options">The query scope's options; <c>null</c> for the default.</param>
        /// <returns>The query scope, which the caller disposes, and the clause's class.</returns>
        public static (Scope Query, Pred Q) Ask(CosmosTheory c, Scope schema, ConstructorTerm<Pred> term, ScopeOptions? options = null)
        {
            var query = schema.CreateChild(c.Query, options);
            var q = query.Make(term);
            query.Assume(c.WhereClause.Of(q));
            var report = query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            Assert.True(report.Saturated);
            Assert.Empty(query.Children);
            return (query, q);
        }

    }

}
