using Xunit;

namespace Eggnog.Tests.Predicates.Scoped
{

    /// <summary>
    /// A schema of documents discriminated by <c>kind</c>, compiled into a schema scope of
    /// <see cref="PredicateTheory"/> as facts assumed in the schema and branch rules local to it. <c>kind</c> is
    /// present and holds a, b or c: the schema is a <c>oneOf</c> discriminated by an enum. Documents of kinds a and b
    /// carry a <c>body.ref</c> stored as a canonical lowercase UUID, documents of kind b also a <c>body.key</c>, and
    /// documents of kind c a <c>body.pos</c> instead. The timestamps under <c>meta</c> are stored as fixed-width
    /// instants.
    /// </summary>
    public static class DiscriminatedSchema
    {

        /// <summary>
        /// The UUID the queries compare <c>body.ref</c> with.
        /// </summary>
        public const string Ref = "11111111-1111-1111-1111-111111111111";

        /// <summary>
        /// The UUID the queries compare <c>body.key</c> with.
        /// </summary>
        public const string Key = "22222222-2222-2222-2222-222222222222";

        /// <summary>
        /// The instant the update-time comparison asks for.
        /// </summary>
        public const string Since = "2026-08-02T12:00:00.000Z";

        /// <summary>
        /// Compiles the schema under the graph's frozen root with the theory's <see cref="PredicateTheory.Facts"/>:
        /// horn and lattice rules, and the schema's split on its discriminator <c>kind</c>, which opens three cases
        /// (a, b, c); then freezes it.
        /// </summary>
        /// <param name="c">The theory, whose graph's root is frozen.</param>
        /// <returns>The frozen schema scope.</returns>
        public static Scope Compile(PredicateTheory c)
        {
            var schema = c.Graph.Root.CreateChild(c.Schema);
            Declare(c, schema);
            var compiled = schema.Run(c.Facts);
            Assert.True(compiled.Saturated);
            Assert.Equal(3, compiled.CasesOpened);
            Assert.Empty(schema.Children);
            schema.Freeze();
            return schema;
        }

        /// <summary>
        /// Declares the schema's facts and branch rules in a writable schema scope.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="schema">The schema scope.</param>
        public static void Declare(PredicateTheory c, Scope schema)
        {
            schema.Assume(
                c.Present.Of("type"),
                c.Types.Of("type").Is(ValueSet.Of("string")),
                c.Present.Of("seq"),
                c.Types.Of("seq").Is(ValueSet.Of("number")),
                c.Lo.Of("seq").Is(1L),
                c.Hi.Of("seq").Is(2147483647L),
                c.Present.Of("kind"),
                c.Dom.Of("kind").Is(ValueSet.Of("a", "b", "c")),
                c.Discriminator.Of("kind"),
                c.Present.Of("uid"),
                c.Form.Of("uid", "uuid-lower"),
                c.Present.Of("label"),
                c.Types.Of("label").Is(ValueSet.Of("string")),
                c.Present.Of("flag"),
                c.Types.Of("flag").Is(ValueSet.Of("boolean")),
                c.Present.Of("meta"),
                c.Form.Of("meta.created", "instant-ms"),
                c.Form.Of("meta.updated", "instant-ms"),
                c.Form.Of("meta.deleted", "instant-ms"));
            c.DeclareBranch(schema, "kind", "a", c.Present.Of("body.ref"), c.Form.Of("body.ref", "uuid-lower"));
            c.DeclareBranch(schema, "kind", "b",
                c.Present.Of("body.ref"),
                c.Form.Of("body.ref", "uuid-lower"),
                c.Present.Of("body.key"),
                c.Form.Of("body.key", "uuid-lower"));
            c.DeclareBranch(schema, "kind", "c", c.Present.Of("body.pos"));
        }

        /// <summary>
        /// The schema's queries: one document of kind b by key; the documents of kinds a and b with one ref, written
        /// as one disjunction and spelt per kind; those of them that are flagged and were updated since an instant;
        /// documents of every kind, a tautology over <c>kind</c>; and the documents with one ref other than those of
        /// kind b, which may be of kind c. Those that need a split of the query to lower do not lower under
        /// <see cref="Ask"/>, which runs none; they still exercise the schema.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <returns>The queries' predicates.</returns>
        public static ConstructorTerm<Pred>[] Queries(PredicateTheory c) =>
        [
            c.And.Of(c.StrEq.Of("kind", "b"), c.UuidEq.Of("body.key", Key)),
            c.And.Of(c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")), c.UuidEq.Of("body.ref", Ref)),
            c.Or.Of(
                c.And.Of(c.StrEq.Of("kind", "a"), c.UuidEq.Of("body.ref", Ref)),
                c.And.Of(c.StrEq.Of("kind", "b"), c.UuidEq.Of("body.ref", Ref))),
            c.And.Of(
                c.And.Of(c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")), c.UuidEq.Of("body.ref", Ref)),
                c.And.Of(c.BoolTrue.Of("flag"), c.InstantGt.Of("meta.updated", Since))),
            c.And.Of(
                c.StrEq.Of("type", "Record"),
                c.Or.Of(c.StrEq.Of("kind", "a"), c.Or.Of(c.StrEq.Of("kind", "b"), c.StrEq.Of("kind", "c")))),
            c.And.Of(c.Not.Of(c.StrEq.Of("kind", "b")), c.UuidEq.Of("body.ref", Ref)),
        ];

        /// <summary>
        /// Asks a query in a new child of the schema, tagged as a query, and saturates it with horn, lattice and
        /// rewrite rules, without a split.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="schema">The frozen schema.</param>
        /// <param name="term">The query's predicate.</param>
        /// <param name="options">The query scope's options; <c>null</c> for the default.</param>
        /// <returns>The query scope, which the caller disposes, and the predicate's class.</returns>
        public static (Scope Query, Pred Q) Ask(PredicateTheory c, Scope schema, ConstructorTerm<Pred> term, ScopeOptions? options = null)
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
