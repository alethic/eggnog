using Xunit;

namespace Eggnog.Tests.Predicates.Scoped
{

    /// <summary>
    /// <see cref="PredicateTheory"/> end to end: what a schema's or a query's case rules derive, what the query's
    /// rewrites then prove, and the predicate extraction returns, with its cost. The queries over a discriminated
    /// schema run in their own query scopes under <see cref="DiscriminatedSchema"/>, whose discriminator <c>kind</c>
    /// holds a, b or c; a UUID or instant comparison costs 1000, so a predicate that still holds one after the run did
    /// not lower.
    /// </summary>
    public sealed class DiscriminatedSchemaTests
    {

        /// <summary>
        /// A graph with the theory, an empty frozen root, and the compiled discriminated schema.
        /// </summary>
        static (PredicateTheory C, Scope Schema) Compiled()
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            return (c, DiscriminatedSchema.Compile(c));
        }

        /// <summary>
        /// Asks a query with the query's case rules (<see cref="PredicateTheory.Saturation"/>) and checks that the run
        /// saturated and left no child behind.
        /// </summary>
        static (Scope Query, Pred Q) AskWithCases(PredicateTheory c, Scope schema, ConstructorTerm<Pred> term)
        {
            var query = schema.CreateChild(c.Query);
            var q = query.Make(term);
            query.Assume(c.WhereClause.Of(q));
            var report = query.Run(c.Saturation);
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Saturated);
            Assert.Empty(query.Children);
            return (query, q);
        }

        /// <summary>
        /// The flagged-updates predicate, with its ref and update-time comparisons given: documents of kinds a and b
        /// with one ref, flagged, updated after an instant.
        /// </summary>
        static ConstructorTerm<Pred> FlaggedUpdates(PredicateTheory c, ConstructorTerm<Pred> reference, ConstructorTerm<Pred> updated) =>
            c.And.Of(
                c.And.Of(c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")), reference),
                c.And.Of(c.BoolTrue.Of("flag"), updated));

        /// <summary>
        /// Branch b declares the UUID form of every document of kind b, so a query that names the kind lowers its UUID
        /// comparison to a string comparison without case rules of its own.
        /// </summary>
        [Fact]
        public void AQueryThatNamesTheKindLowersWithoutCaseRules()
        {
            var (c, schema) = Compiled();
            var (query, q) = DiscriminatedSchema.Ask(c, schema, c.And.Of(c.StrEq.Of("kind", "b"), c.UuidEq.Of("body.key", DiscriminatedSchema.Key)));
            using (query)
            {
                Assert.True(query.AreEqual(q, c.And.Of(c.StrEq.Of("kind", "b"), c.StrEq.Of("body.key", DiscriminatedSchema.Key))));
                Assert.True(query.Check(c.Form.Of("body.key", "uuid-lower")));
                Assert.False(schema.Check(c.Form.Of("body.key", "uuid-lower")));
                var best = query.Extract(q);
                Assert.Equal($"(And (StrEq \"kind\" \"b\") (StrEq \"body.key\" \"{DiscriminatedSchema.Key}\"))", best.ToString());
                Assert.Equal<ulong>(7, best.Cost);
            }
        }

        /// <summary>
        /// A disjunction over every value of a discriminator's domain is a tautology, and the conjunction drops it.
        /// </summary>
        [Fact]
        public void ADisjunctionOverTheWholeDomainIsDropped()
        {
            var (c, schema) = Compiled();
            var (query, q) = DiscriminatedSchema.Ask(c, schema, c.And.Of(
                c.StrEq.Of("type", "Record"),
                c.Or.Of(c.StrEq.Of("kind", "a"), c.Or.Of(c.StrEq.Of("kind", "b"), c.StrEq.Of("kind", "c")))));
            using (query)
            {
                Assert.True(query.AreEqual(q, c.StrEq.Of("type", "Record")));
                var best = query.Extract(q);
                Assert.Equal("(StrEq \"type\" \"Record\")", best.ToString());
                Assert.Equal<ulong>(3, best.Cost);
            }
        }

        /// <summary>
        /// Excluding kind b leaves a and c, and kind c declares no ref, so nothing proves its form and the UUID
        /// comparison stays.
        /// </summary>
        [Fact]
        public void AComparisonThatOneRemainingKindDoesNotGovernDoesNotLower()
        {
            var (c, schema) = Compiled();
            var (query, q) = DiscriminatedSchema.Ask(c, schema, c.And.Of(c.Not.Of(c.StrEq.Of("kind", "b")), c.UuidEq.Of("body.ref", DiscriminatedSchema.Ref)));
            using (query)
            {
                Assert.False(query.Check(c.Form.Of("body.ref", "uuid-lower")));
                Assert.Equal(Set.Of(["a", "c"]), query.Get(c.Dom.Of("kind")));
                var best = query.Extract(q);
                Assert.True(best.Mentions(c.UuidEq));
                Assert.Equal($"(And (Not (StrEq \"kind\" \"b\")) (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\"))", best.ToString());
                Assert.Equal<ulong>(1007, best.Cost);
            }
        }

        /// <summary>
        /// The query narrows <c>kind</c> to a and b, whose branches both declare the ref's form: the query's case rules
        /// derive it in the query, not in the schema, and the comparison lowers.
        /// </summary>
        [Fact]
        public void ADisjunctionOfKindsLowersThroughTheQuerysCaseRules()
        {
            var (c, schema) = Compiled();
            var (query, q) = AskWithCases(c, schema,
                c.And.Of(c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")), c.UuidEq.Of("body.ref", DiscriminatedSchema.Ref)));
            using (query)
            {
                Assert.True(query.Check(c.Form.Of("body.ref", "uuid-lower")));
                Assert.False(schema.Check(c.Form.Of("body.ref", "uuid-lower")));
                Assert.True(query.AreEqual(q, c.And.Of(
                    c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")),
                    c.StrEq.Of("body.ref", DiscriminatedSchema.Ref))));
                var best = query.Extract(q);
                Assert.Equal(
                    $"(And (Or (StrEq \"kind\" \"a\") (StrEq \"kind\" \"b\")) (StrEq \"body.ref\" \"{DiscriminatedSchema.Ref}\"))",
                    best.ToString());
                Assert.Equal<ulong>(11, best.Cost);
            }
        }

        /// <summary>
        /// The same query spelt as one conjunction per kind: the domain narrows to a and b, and both comparisons lower.
        /// </summary>
        [Fact]
        public void ADisjunctionSpeltPerKindLowersThroughTheQuerysCaseRules()
        {
            var (c, schema) = Compiled();
            var (query, q) = AskWithCases(c, schema, c.Or.Of(
                c.And.Of(c.StrEq.Of("kind", "a"), c.UuidEq.Of("body.ref", DiscriminatedSchema.Ref)),
                c.And.Of(c.StrEq.Of("kind", "b"), c.UuidEq.Of("body.ref", DiscriminatedSchema.Ref))));
            using (query)
            {
                Assert.Equal(Set.Of(["a", "b"]), query.Get(c.Dom.Of("kind")));
                Assert.True(query.Check(c.Form.Of("body.ref", "uuid-lower")));
                Assert.True(query.AreEqual(q, c.Or.Of(
                    c.And.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("body.ref", DiscriminatedSchema.Ref)),
                    c.And.Of(c.StrEq.Of("kind", "b"), c.StrEq.Of("body.ref", DiscriminatedSchema.Ref)))));

                // The two spellings of the second conjunction cost the same. Extraction breaks the tie as egglog's
                // extractor does, by the order in which its passes meet the rows, which here gives the spelling asked
                // for.
                var best = query.Extract(q);
                Assert.Equal(
                    $"(Or (And (StrEq \"kind\" \"a\") (StrEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")) (And (StrEq \"kind\" \"b\") (StrEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")))",
                    best.ToString());
                Assert.Equal<ulong>(15, best.Cost);
            }
        }

        /// <summary>
        /// Nested in a larger conjunction, the case rules still lower the UUID comparison, and the instant comparison
        /// lowers to a string comparison on the schema's fixed-width form.
        /// </summary>
        [Fact]
        public void EveryCostlyComparisonOfANestedPredicateLowers()
        {
            var (c, schema) = Compiled();
            var (query, q) = AskWithCases(c, schema, FlaggedUpdates(c,
                c.UuidEq.Of("body.ref", DiscriminatedSchema.Ref), c.InstantGt.Of("meta.updated", DiscriminatedSchema.Since)));
            using (query)
            {
                Assert.True(query.AreEqual(q, FlaggedUpdates(c,
                    c.StrEq.Of("body.ref", DiscriminatedSchema.Ref), c.StrGt.Of("meta.updated", DiscriminatedSchema.Since))));
                Assert.True(query.Check(c.Form.Of("body.ref", "uuid-lower")));
                var best = query.Extract(q);
                Assert.False(best.Mentions(c.UuidEq));
                Assert.False(best.Mentions(c.InstantGt));
                Assert.Equal(
                    $"(And (And (Or (StrEq \"kind\" \"a\") (StrEq \"kind\" \"b\")) (StrEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")) (And (BoolTrue \"flag\") (StrGt \"meta.updated\" \"{DiscriminatedSchema.Since}\")))",
                    best.ToString());
                Assert.Equal<ulong>(18, best.Cost);
            }
        }

        /// <summary>
        /// A disjunction with no discriminator of its own, modelled with a hidden one: a synthesized key
        /// <c>$anyOf/0</c>, present, whose domain is the branch indices, marked as a discriminator, with branches on
        /// its value. Both branches declare a domain of <c>status</c>, so the schema's case rules derive the union of
        /// the branch domains; neither branch's own field holds in the schema. A query that compares status with a
        /// value outside the union is refuted; it mentions <c>status</c>, not the key, so its case rules derive nothing
        /// more.
        /// </summary>
        [Fact]
        public void TheCaseRulesOfAHiddenDiscriminatorJoinTheBranchDomains()
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema);
            schema.Assume(c.Present.Of("$anyOf/0"), c.Dom.Of("$anyOf/0").Is(Set.Of(["0", "1"])), c.Discriminator.Of("$anyOf/0"));
            c.DeclareBranch(schema, "$anyOf/0", "0", present: ["legacyId"], domains: [("status", Set.Of(["active", "archived"]))]);
            c.DeclareBranch(schema, "$anyOf/0", "1", present: ["id2"], domains: [("status", Set.Of(["draft", "active"]))]);

            var compiled = schema.Run(c.Facts);
            Assert.True(compiled.Saturated);
            Assert.Empty(schema.Children);
            Assert.Equal(Set.Of(["active", "archived", "draft"]), schema.Get(c.Dom.Of("status")));
            Assert.False(schema.Check(c.Present.Of("legacyId")));
            Assert.False(schema.Check(c.Present.Of("id2")));
            schema.Freeze();

            var (query, q) = AskWithCases(c, schema, c.StrEq.Of("status", "purged"));
            using (query)
            {
                Assert.True(c.IsContradictory(query));
                Assert.True(query.AreEqual(q, c.PFalse.Of()));
                var best = query.Extract(q);
                Assert.Equal("(PFalse)", best.ToString());
                Assert.Equal<ulong>(0, best.Cost);
            }
        }

    }

}
