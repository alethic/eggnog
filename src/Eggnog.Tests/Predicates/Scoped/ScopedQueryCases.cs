using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Predicates.Scoped
{

    /// <summary>
    /// Queries of <see cref="PredicateTheory"/> that run without a split, each a <see cref="ScopedQueryCase"/> over a
    /// schema of its own, with the steps that compile a case's schema and ask its query; the queries over a schema with
    /// a discriminator split are <see cref="DiscriminatedSchema"/>'s. Between them they exercise stored forms and
    /// lowering, discriminated and undiscriminated unions, refused values, numeric bounds, integer domains, type claims
    /// and the deletion of conjuncts the schema implies. Used by the tests that must run every query in another
    /// setting: two graphs in either order and on parallel threads (ScopeTagTests), and a degree of parallelism above 1
    /// (ParallelEquivalenceTests).
    /// </summary>
    public static class ScopedQueryCases
    {

        /// <summary>
        /// The UUID the cases compare.
        /// </summary>
        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        /// <summary>
        /// The timestamp the weakened ordering comparison compares.
        /// </summary>
        const string At = "2024-01-01T00:00:00Z";

        /// <summary>
        /// Gets the cases.
        /// </summary>
        public static IReadOnlyList<ScopedQueryCase> All { get; } =
        [
            new("a declared stored form lowers the UUID comparison",
                (c, s) => c.DeclareBranch(s, "kind", "B", c.Form.Of("ref", "uuid-lower")),
                c => c.And.Of(c.StrEq.Of("kind", "B"), c.UuidEq.Of("ref", Ref)),
                (c, s, q, p) => q.Extract(p).ToString() == $"(And (StrEq \"kind\" \"B\") (StrEq \"ref\" \"{Ref}\"))"),
            new("without the discriminator nothing is proven and nothing lowers",
                (c, s) => c.DeclareBranch(s, "kind", "B", c.Form.Of("ref", "uuid-lower")),
                c => c.UuidEq.Of("ref", Ref),
                (c, s, q, p) => q.Check(c.Form.Of("ref", "uuid-lower")) == false && q.Extract(p).Cost == 1002),
            new("without a split, horn and lattice rules alone do not lower a disjunction",
                (c, s) =>
                {
                    s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of("order", "invoice", "note")), c.Discriminator.Of("kind"));
                    c.DeclareBranch(s, "kind", "order", c.Form.Of("ref", "uuid-lower"));
                    c.DeclareBranch(s, "kind", "invoice", c.Form.Of("ref", "uuid-lower"));
                },
                c => c.And.Of(c.Or.Of(c.StrEq.Of("kind", "order"), c.StrEq.Of("kind", "invoice")), c.UuidEq.Of("ref", Ref)),
                (c, s, q, p) => q.Get(c.Dom.Of("kind")).Equals(Set.Of("invoice", "order")) && q.Check(c.Form.Of("ref", "uuid-lower")) == false),
            new("a domain less a refused value proves the guard",
                (c, s) =>
                {
                    s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of("order", "invoice")));
                    c.DeclareBranch(s, "kind", "order", c.Form.Of("ref", "uuid-lower"));
                },
                c => c.And.Of(c.Not.Of(c.StrEq.Of("kind", "invoice")), c.UuidEq.Of("ref", Ref)),
                (c, s, q, p) => q.Extract(p).ToString() == $"(And (Not (StrEq \"kind\" \"invoice\")) (StrEq \"ref\" \"{Ref}\"))"),
            new("without a split, the branches of an undiscriminated union never meet",
                (c, s) =>
                {
                    s.Assume(c.Present.Of("$anyOf/0"), c.Dom.Of("$anyOf/0").Is(Set.Of("0", "1")), c.Discriminator.Of("$anyOf/0"));
                    c.DeclareBranch(s, "$anyOf/0", "0", c.Present.Of("legacyId"), c.Dom.Of("status").Is(Set.Of("active", "archived")));
                    c.DeclareBranch(s, "$anyOf/0", "1", c.Present.Of("id2"), c.Dom.Of("status").Is(Set.Of("draft", "active")));
                },
                c => c.StrEq.Of("status", "purged"),
                (c, s, q, p) => q.IsContradictory == false && q.Extract(p).ToString() == "(StrEq \"status\" \"purged\")"),
            new("a comparison beyond a declared bound is false",
                (c, s) => s.Assume(c.Types.Of("n").Is(Set.Of("number")), c.Lo.Of("n").Is(0L), c.Hi.Of("n").Is(100L)),
                c => c.NumGt.Of("n", 500L),
                (c, s, q, p) => q.IsContradictory && s.IsContradictory == false && q.Extract(p).ToString() == "(PFalse)"),
            new("a bound inside the declared range is left alone",
                (c, s) => s.Assume(c.Types.Of("n").Is(Set.Of("number")), c.Lo.Of("n").Is(0L), c.Hi.Of("n").Is(100L)),
                c => c.NumGt.Of("n", 50L),
                (c, s, q, p) => q.Get(c.Lo.Of("n")) == 51L && q.Extract(p).ToString() == "(NumGt \"n\" 50)"),
            new("a declared type makes a weakened ordering comparison exact",
                (c, s) => s.Assume(c.Types.Of("at").Is(Set.Of("string"))),
                Weakened,
                (c, s, q, p) => q.Extract(p).ToString() == $"(StrGe \"at\" \"{At}\")"),
            new("without a declared type the comparison stays weakened",
                (c, s) => { },
                Weakened,
                (c, s, q, p) => q.Extract(p).ToString() == $"(And (IsDefined \"at\") (Or (Not (IsString \"at\")) (StrGe \"at\" \"{At}\")))"),
            new("a declared tautology is deleted",
                (c, s) => s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of("A"))),
                c => c.And.Of(c.StrEq.Of("kind", "A"), c.StrEq.Of("id", "x")),
                (c, s, q, p) => q.Extract(p).ToString() == "(StrEq \"id\" \"x\")"),
            new("without a presence claim the comparison stays",
                (c, s) => s.Assume(c.Dom.Of("kind").Is(Set.Of("A"))),
                c => c.And.Of(c.StrEq.Of("kind", "A"), c.StrEq.Of("id", "x")),
                (c, s, q, p) => q.Extract(p).ToString() == "(And (StrEq \"kind\" \"A\") (StrEq \"id\" \"x\"))"),
            new("an integer outside a declared domain is false",
                (c, s) => s.Assume(c.Present.Of("n"), c.IDom.Of("n").Is(Set.Of(100L, 200L, 300L))),
                c => c.And.Of(c.NumGt.Of("n", 499L), c.NumLt.Of("n", 501L)),
                (c, s, q, p) => q.IsContradictory && q.Extract(p).ToString() == "(PFalse)"),
            new("an integer inside the declared domain is left alone",
                (c, s) => s.Assume(c.Present.Of("n"), c.IDom.Of("n").Is(Set.Of(100L, 500L))),
                c => c.And.Of(c.NumGt.Of("n", 499L), c.NumLt.Of("n", 501L)),
                (c, s, q, p) => q.IsContradictory == false && q.Get(c.IDom.Of("n")).Equals(Set.Of(500L))),
        ];

        /// <summary>
        /// Compiles a case's schema under the graph's frozen root: declares it in a new schema scope, saturates horn
        /// and lattice rules, and freezes it.
        /// </summary>
        /// <param name="c">The theory, whose graph's root is frozen.</param>
        /// <param name="queryCase">The case.</param>
        /// <param name="options">The schema's options; <c>null</c> for the default.</param>
        /// <returns>The frozen schema.</returns>
        public static Scope Compile(PredicateTheory c, ScopedQueryCase queryCase, ScopeOptions? options = null)
        {
            var schema = c.Graph.Root.CreateChild(c.Schema, options);
            queryCase.Declare(c, schema);
            Assert.True(schema.Run(Schedule.Saturate(c.Horn, c.Lattice)).Saturated, queryCase.Name);
            schema.Freeze();
            return schema;
        }

        /// <summary>
        /// Asks a case's query of its compiled schema (<see cref="DiscriminatedSchema.Ask"/>) and returns whether its
        /// result holds.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="schema">The case's frozen schema.</param>
        /// <param name="queryCase">The case.</param>
        /// <returns>Whether the result holds.</returns>
        public static bool Holds(PredicateTheory c, Scope schema, ScopedQueryCase queryCase)
        {
            var (query, q) = DiscriminatedSchema.Ask(c, schema, queryCase.Query(c));
            using (query)
                return queryCase.Holds(c, schema, query, q);
        }

        /// <summary>
        /// Compiles every case's schema and <see cref="DiscriminatedSchema"/>'s in one graph, under its frozen root
        /// (frozen here if it is not yet).
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <returns>The frozen schemas, one per case, then the discriminated schema.</returns>
        public static Scope[] CompileAll(PredicateTheory c)
        {
            c.Graph.Root.Freeze();
            return All.Select(queryCase => Compile(c, queryCase)).Append(DiscriminatedSchema.Compile(c)).ToArray();
        }

        /// <summary>
        /// Asks every case's query of the schemas <see cref="CompileAll"/> made, and every query of the discriminated
        /// schema; returns the cases whose result does not hold, and each discriminated-schema query's extraction.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="schemas">The schemas.</param>
        /// <returns>The names of the cases that failed, then one line per discriminated-schema query.</returns>
        public static List<string> AskAll(PredicateTheory c, Scope[] schemas)
        {
            var lines = new List<string>();
            for (var i = 0; i < All.Count; i++)
                if (Holds(c, schemas[i], All[i]) == false)
                    lines.Add("failed: " + All[i].Name);

            foreach (var term in DiscriminatedSchema.Queries(c))
            {
                var (query, q) = DiscriminatedSchema.Ask(c, schemas[^1], term);
                using (query)
                    lines.Add(query.Extract(q).ToString());
            }

            return lines;
        }

        /// <summary>
        /// A weakened ordering comparison: <c>at</c> is present, and holds either no string or a string ordered at or
        /// after <see cref="At"/>.
        /// </summary>
        static ConstructorTerm<Pred> Weakened(PredicateTheory c) =>
            c.And.Of(c.IsDefined.Of("at"), c.Or.Of(c.Not.Of(c.IsString.Of("at")), c.StrGe.Of("at", At)));

    }

}
