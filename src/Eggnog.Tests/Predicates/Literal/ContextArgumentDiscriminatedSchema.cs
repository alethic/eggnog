using Xunit;

namespace Eggnog.Tests.Predicates.Literal
{

    /// <summary>
    /// The schema of <see cref="Scoped.DiscriminatedSchema"/> in <see cref="ContextArgumentTheory"/>: its facts written
    /// in the <c>(Schema)</c> context at the root, and the three branch rules of its discriminator <c>kind</c> as
    /// <c>horn</c> rules over any context. <c>kind</c> is present and holds a, b or c; documents of kinds a and b carry
    /// a <c>body.ref</c> stored as a canonical lowercase UUID, documents of kind b also a <c>body.key</c>, and
    /// documents of kind c a <c>body.pos</c> instead.
    /// </summary>
    public static class ContextArgumentDiscriminatedSchema
    {

        /// <summary>The UUID the queries compare <c>body.ref</c> with.</summary>
        public const string Ref = "11111111-1111-1111-1111-111111111111";

        /// <summary>The instant the update-time comparison asks for.</summary>
        public const string Since = "2026-08-02T12:00:00.000Z";

        /// <summary>
        /// Creates the theory in a new graph, grounded, with the schema's facts and branch rules.
        /// </summary>
        /// <returns>The theory.</returns>
        public static ContextArgumentTheory Create()
        {
            var c = new ContextArgumentTheory(new EGraph());
            c.Ground();
            var schema = c.Schema.Of();
            c.Graph.Root.Assume(
                c.Present.Of(schema, "type"),
                c.Types.Of(schema, "type").Is(ValueSet.Of("string")),
                c.Present.Of(schema, "seq"),
                c.Types.Of(schema, "seq").Is(ValueSet.Of("number")),
                c.Lo.Of(schema, "seq").Is(1L),
                c.Hi.Of(schema, "seq").Is(2147483647L),
                c.Present.Of(schema, "kind"),
                c.Dom.Of(schema, "kind").Is(ValueSet.Of("a", "b", "c")),
                c.Discriminator.Of("kind", "a"),
                c.Discriminator.Of("kind", "b"),
                c.Discriminator.Of("kind", "c"),
                c.Present.Of(schema, "uid"),
                c.Form.Of(schema, "uid", "uuid-lower"),
                c.Present.Of(schema, "label"),
                c.Types.Of(schema, "label").Is(ValueSet.Of("string")),
                c.Present.Of(schema, "flag"),
                c.Types.Of(schema, "flag").Is(ValueSet.Of("boolean")),
                c.Present.Of(schema, "meta"),
                c.Form.Of(schema, "meta.created", "instant-ms"),
                c.Form.Of(schema, "meta.updated", "instant-ms"),
                c.Form.Of(schema, "meta.deleted", "instant-ms"));
            c.Horn.DefineRule("kind = a", r =>
            {
                var x = r.Var<Ctx>("c");
                r.When(c.Present.Of(x, "kind"), c.Dom.Of(x, "kind").Is(ValueSet.Of("a")));
                r.Then(c.Present.Of(x, "body.ref"), c.Form.Of(x, "body.ref", "uuid-lower"));
            });
            c.Horn.DefineRule("kind = b", r =>
            {
                var x = r.Var<Ctx>("c");
                r.When(c.Present.Of(x, "kind"), c.Dom.Of(x, "kind").Is(ValueSet.Of("b")));
                r.Then(
                    c.Present.Of(x, "body.ref"),
                    c.Form.Of(x, "body.ref", "uuid-lower"),
                    c.Present.Of(x, "body.key"),
                    c.Form.Of(x, "body.key", "uuid-lower"));
            });
            c.Horn.DefineRule("kind = c", r =>
            {
                var x = r.Var<Ctx>("c");
                r.When(c.Present.Of(x, "kind"), c.Dom.Of(x, "kind").Is(ValueSet.Of("c")));
                r.Then(c.Present.Of(x, "body.pos"));
            });
            return c;
        }

        /// <summary>
        /// Asks a query at the root: makes its predicate, asserts it as the query's, and runs
        /// <c>(saturate horn lattice split rewrite)</c>, which must saturate.
        /// </summary>
        /// <param name="c">The theory, from <see cref="Create"/>.</param>
        /// <param name="term">The query's predicate.</param>
        /// <returns>The predicate's class.</returns>
        public static Pred Ask(ContextArgumentTheory c, ConstructorTerm<Pred> term)
        {
            var root = c.Graph.Root;
            var q = root.Make(term);
            root.Assume(c.WhereClause.Of(q));
            var report = root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));
            Assert.True(report.Saturated);
            return q;
        }

        /// <summary>
        /// The flagged documents with one ref updated since an instant: documents of kinds a and b, written as one
        /// disjunction, whose ref is the given one, that are flagged and whose update time is after the instant.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="uuid">The ref comparison, or its lowering.</param>
        /// <param name="instant">The update-time comparison, or its lowering.</param>
        /// <returns>The predicate.</returns>
        public static ConstructorTerm<Pred> FlaggedUpdates(ContextArgumentTheory c, Term<Pred> uuid, Term<Pred> instant) =>
            c.And.Of(
                c.And.Of(c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")), uuid),
                c.And.Of(c.BoolTrue.Of("flag"), instant));

    }

}
