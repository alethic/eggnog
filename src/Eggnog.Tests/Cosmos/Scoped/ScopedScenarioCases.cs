using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Cosmos.Scoped
{

    /// <summary>
    /// Every query of the scoped Cosmos scenarios 01 to 06 and 08 that runs without a split, as a
    /// <see cref="ScopedScenarioCase"/>, with the steps that compile a case's container and ask its query; scenario
    /// 07's queries are <see cref="SweepLinks"/>'s. Used by the tests that must run every scenario in another setting:
    /// two graphs in either order and on parallel threads (ScopeTagTests), and a degree of parallelism above 1
    /// (ParallelEquivalenceTests).
    /// </summary>
    public static class ScopedScenarioCases
    {

        /// <summary>
        /// The UUID the scenarios compare.
        /// </summary>
        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        /// <summary>
        /// The timestamp scenario 06 compares.
        /// </summary>
        const string At = "2024-01-01T00:00:00Z";

        /// <summary>
        /// Gets the cases, in scenario order.
        /// </summary>
        public static IReadOnlyList<ScopedScenarioCase> All { get; } =
        [
            new("01 ADeclaredStoredFormPutsTheComparisonInTheStatement",
                (c, s) => c.DeclareBranch(s, "kind", "B", c.Form.Of("ref", "uuid-lower")),
                c => c.And.Of(c.StrEq.Of("kind", "B"), c.UuidEq.Of("ref", Ref)),
                (c, s, q, p) => q.Extract(p).ToString() == $"(And (StrEq \"kind\" \"B\") (StrEq \"ref\" \"{Ref}\"))"),
            new("01 WithoutTheDiscriminatorNothingIsProvenAndNothingLowers",
                (c, s) => c.DeclareBranch(s, "kind", "B", c.Form.Of("ref", "uuid-lower")),
                c => c.UuidEq.Of("ref", Ref),
                (c, s, q, p) => q.Check(c.Form.Of("ref", "uuid-lower")) == false && q.Extract(p).Cost == 1002),
            new("02 HornAndLatticesAloneDoNotLower",
                (c, s) =>
                {
                    s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("order", "invoice", "note")), c.Discriminator.Of("kind"));
                    c.DeclareBranch(s, "kind", "order", c.Form.Of("ref", "uuid-lower"));
                    c.DeclareBranch(s, "kind", "invoice", c.Form.Of("ref", "uuid-lower"));
                },
                c => c.And.Of(c.Or.Of(c.StrEq.Of("kind", "order"), c.StrEq.Of("kind", "invoice")), c.UuidEq.Of("ref", Ref)),
                (c, s, q, p) => q.Get(c.Dom.Of("kind")).Equals(ValueSet.Of("invoice", "order")) && q.Check(c.Form.Of("ref", "uuid-lower")) == false),
            new("03 ADomainLessARefusedValueProvesTheGuard",
                (c, s) =>
                {
                    s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("order", "invoice")));
                    c.DeclareBranch(s, "kind", "order", c.Form.Of("ref", "uuid-lower"));
                },
                c => c.And.Of(c.Not.Of(c.StrEq.Of("kind", "invoice")), c.UuidEq.Of("ref", Ref)),
                (c, s, q, p) => q.Extract(p).ToString() == $"(And (Not (StrEq \"kind\" \"invoice\")) (StrEq \"ref\" \"{Ref}\"))"),
            new("04 WithoutTheSplitTheBranchesNeverMeet",
                (c, s) =>
                {
                    s.Assume(c.Present.Of("$anyOf/0"), c.Dom.Of("$anyOf/0").Is(ValueSet.Of("0", "1")), c.Discriminator.Of("$anyOf/0"));
                    c.DeclareBranch(s, "$anyOf/0", "0", c.Present.Of("legacyId"), c.Dom.Of("status").Is(ValueSet.Of("active", "archived")));
                    c.DeclareBranch(s, "$anyOf/0", "1", c.Present.Of("id2"), c.Dom.Of("status").Is(ValueSet.Of("draft", "active")));
                },
                c => c.StrEq.Of("status", "purged"),
                (c, s, q, p) => q.IsContradictory == false && q.Extract(p).ToString() == "(StrEq \"status\" \"purged\")"),
            new("05 AComparisonBeyondADeclaredBoundIsFalse",
                (c, s) => s.Assume(c.Types.Of("n").Is(ValueSet.Of("number")), c.Lo.Of("n").Is(0L), c.Hi.Of("n").Is(100L)),
                c => c.NumGt.Of("n", 500L),
                (c, s, q, p) => q.IsContradictory && s.IsContradictory == false && q.Extract(p).ToString() == "(PFalse)"),
            new("05 ABoundInsideTheRangeIsLeftAlone",
                (c, s) => s.Assume(c.Types.Of("n").Is(ValueSet.Of("number")), c.Lo.Of("n").Is(0L), c.Hi.Of("n").Is(100L)),
                c => c.NumGt.Of("n", 50L),
                (c, s, q, p) => q.Get(c.Lo.Of("n")) == 51L && q.Extract(p).ToString() == "(NumGt \"n\" 50)"),
            new("06 ADeclaredTypeMakesAnOrderingComparisonExact",
                (c, s) => s.Assume(c.Types.Of("at").Is(ValueSet.Of("string"))),
                Weakened,
                (c, s, q, p) => q.Extract(p).ToString() == $"(StrGe \"at\" \"{At}\")"),
            new("06 WithoutADeclaredTypeTheComparisonIsStillWeakened",
                (c, s) => { },
                Weakened,
                (c, s, q, p) => q.Extract(p).ToString() == $"(And (IsDefined \"at\") (Or (Not (IsString \"at\")) (StrGe \"at\" \"{At}\")))"),
            new("06 ADeclaredTautologyIsNotAsked",
                (c, s) => s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("A"))),
                c => c.And.Of(c.StrEq.Of("kind", "A"), c.StrEq.Of("id", "x")),
                (c, s, q, p) => q.Extract(p).ToString() == "(StrEq \"id\" \"x\")"),
            new("06 WithoutAPresenceClaimTheComparisonStays",
                (c, s) => s.Assume(c.Dom.Of("kind").Is(ValueSet.Of("A"))),
                c => c.And.Of(c.StrEq.Of("kind", "A"), c.StrEq.Of("id", "x")),
                (c, s, q, p) => q.Extract(p).ToString() == "(And (StrEq \"kind\" \"A\") (StrEq \"id\" \"x\"))"),
            new("08 AnIntegerOutsideADeclaredDomainIsFalse",
                (c, s) => s.Assume(c.Present.Of("n"), c.IDom.Of("n").Is(ValueSet.Of(100L, 200L, 300L))),
                c => c.And.Of(c.NumGt.Of("n", 499L), c.NumLt.Of("n", 501L)),
                (c, s, q, p) => q.IsContradictory && q.Extract(p).ToString() == "(PFalse)"),
            new("08 AnIntegerInsideTheDomainIsLeftAlone",
                (c, s) => s.Assume(c.Present.Of("n"), c.IDom.Of("n").Is(ValueSet.Of(100L, 500L))),
                c => c.And.Of(c.NumGt.Of("n", 499L), c.NumLt.Of("n", 501L)),
                (c, s, q, p) => q.IsContradictory == false && q.Get(c.IDom.Of("n")).Equals(ValueSet.Of(500L))),
        ];

        /// <summary>
        /// Compiles a case's container under the graph's frozen root: declares it in a new schema scope, saturates horn
        /// and lattice rules, and freezes it.
        /// </summary>
        /// <param name="c">The theory, whose graph's root is frozen.</param>
        /// <param name="scenario">The case.</param>
        /// <param name="options">The schema's options; <c>null</c> for the default.</param>
        /// <returns>The frozen schema.</returns>
        public static Scope Compile(CosmosTheory c, ScopedScenarioCase scenario, ScopeOptions? options = null)
        {
            var schema = c.Graph.Root.CreateChild(c.Schema, options);
            scenario.Declare(c, schema);
            Assert.True(schema.Run(Schedule.Saturate(c.Horn, c.Lattice)).Saturated, scenario.Name);
            schema.Freeze();
            return schema;
        }

        /// <summary>
        /// Asks a case's query of its compiled container (<see cref="SweepLinks.Ask"/>) and returns whether its result
        /// holds.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="schema">The case's frozen schema.</param>
        /// <param name="scenario">The case.</param>
        /// <returns>Whether the result holds.</returns>
        public static bool Holds(CosmosTheory c, Scope schema, ScopedScenarioCase scenario)
        {
            var (query, q) = SweepLinks.Ask(c, schema, scenario.Query(c));
            using (query)
                return scenario.Holds(c, schema, query, q);
        }

        /// <summary>
        /// Compiles every case's container and sweep's in one graph, under its frozen root (frozen here if it is not
        /// yet).
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <returns>The frozen schemas, one per case, then sweep's.</returns>
        public static Scope[] CompileAll(CosmosTheory c)
        {
            c.Graph.Root.Freeze();
            return All.Select(scenario => Compile(c, scenario)).Append(SweepLinks.Compile(c)).ToArray();
        }

        /// <summary>
        /// Asks every case's query of the schemas <see cref="CompileAll"/> made, and every sweep query; returns the
        /// cases whose result does not hold, and each sweep query's extraction.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="schemas">The schemas.</param>
        /// <returns>The names of the cases that failed, then one line per sweep query.</returns>
        public static List<string> AskAll(CosmosTheory c, Scope[] schemas)
        {
            var lines = new List<string>();
            for (var i = 0; i < All.Count; i++)
                if (Holds(c, schemas[i], All[i]) == false)
                    lines.Add("failed: " + All[i].Name);

            foreach (var term in SweepLinks.Queries(c))
            {
                var (query, q) = SweepLinks.Ask(c, schemas[^1], term);
                using (query)
                    lines.Add(query.Extract(q).ToString());
            }

            return lines;
        }

        /// <summary>
        /// Scenario 06's weakened ordering comparison.
        /// </summary>
        static ConstructorTerm<Pred> Weakened(CosmosTheory c) =>
            c.And.Of(c.IsDefined.Of("at"), c.Or.Of(c.Not.Of(c.IsString.Of("at")), c.StrGe.Of("at", At)));

    }

}
