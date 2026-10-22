using Eggnog.Tests.Predicates;
using Eggnog.Tests.Predicates.Scoped;

using Xunit;

namespace Eggnog.Tests.Cases
{

    /// <summary>
    /// Splits end to end on <see cref="PredicateTheory"/>: what a schema's or a query's split lifts, what the query's
    /// rewrites then prove, and the predicate extraction returns, with its cost. The queries over a discriminated
    /// schema run in their own query scopes under <see cref="DiscriminatedSchema"/>, whose compile split <c>kind</c>
    /// three ways (a, b, c); a UUID or instant comparison costs 1000, so a predicate that still holds one after the
    /// run did not lower.
    /// </summary>
    public sealed class SplitExtractionTests
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
        /// Asks a query with the query split (<see cref="PredicateTheory.Saturation"/>) and checks that the run
        /// saturated and left no case behind.
        /// </summary>
        static (Scope Query, Pred Q, RunReport Report) AskWithSplit(PredicateTheory c, Scope schema, ConstructorTerm<Pred> term)
        {
            var query = schema.CreateChild(c.Query);
            var q = query.Make(term);
            query.Assume(c.WhereClause.Of(q));
            var report = query.Run(c.Saturation);
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Saturated);
            Assert.Empty(query.Children);
            return (query, q, report);
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
        /// The schema split lifted branch b's UUID form to every document of kind b, so a query that names the kind
        /// lowers its UUID comparison to a string comparison without a split of its own.
        /// </summary>
        [Fact]
        public void AQueryThatNamesTheKindLowersWithoutASplit()
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
                Assert.Equal(7, best.Cost);
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
                Assert.Equal(3, best.Cost);
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
                Assert.Equal(ValueSet.Of("a", "c"), query.Get(c.Dom.Of("kind")));
                var best = query.Extract(q);
                Assert.True(best.Mentions(c.UuidEq));
                Assert.Equal($"(And (Not (StrEq \"kind\" \"b\")) (UuidEq \"body.ref\" \"{DiscriminatedSchema.Ref}\"))", best.ToString());
                Assert.Equal(1007, best.Cost);
            }
        }

        /// <summary>
        /// The query split opens the cases a and b (c is excluded before the split); both prove the ref's form, which
        /// is lifted to the query, not to the schema, and the comparison lowers.
        /// </summary>
        [Fact]
        public void ADisjunctionOfKindsLowersThroughTheQuerySplit()
        {
            var (c, schema) = Compiled();
            var (query, q, report) = AskWithSplit(c, schema,
                c.And.Of(c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")), c.UuidEq.Of("body.ref", DiscriminatedSchema.Ref)));
            using (query)
            {
                Assert.Equal(2, report.CasesOpened);
                Assert.True(query.Check(c.Form.Of("body.ref", "uuid-lower")));
                Assert.False(schema.Check(c.Form.Of("body.ref", "uuid-lower")));
                Assert.True(query.AreEqual(q, c.And.Of(
                    c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")),
                    c.StrEq.Of("body.ref", DiscriminatedSchema.Ref))));
                var best = query.Extract(q);
                Assert.Equal(
                    $"(And (Or (StrEq \"kind\" \"a\") (StrEq \"kind\" \"b\")) (StrEq \"body.ref\" \"{DiscriminatedSchema.Ref}\"))",
                    best.ToString());
                Assert.Equal(11, best.Cost);
            }
        }

        /// <summary>
        /// The same query spelt as one conjunction per kind: the split narrows the domain to a and b, and both
        /// comparisons lower.
        /// </summary>
        [Fact]
        public void ADisjunctionSpeltPerKindLowersThroughTheQuerySplit()
        {
            var (c, schema) = Compiled();
            var (query, q, report) = AskWithSplit(c, schema, c.Or.Of(
                c.And.Of(c.StrEq.Of("kind", "a"), c.UuidEq.Of("body.ref", DiscriminatedSchema.Ref)),
                c.And.Of(c.StrEq.Of("kind", "b"), c.UuidEq.Of("body.ref", DiscriminatedSchema.Ref))));
            using (query)
            {
                Assert.Equal(2, report.CasesOpened);
                Assert.Equal(ValueSet.Of("a", "b"), query.Get(c.Dom.Of("kind")));
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
                Assert.Equal(15, best.Cost);
            }
        }

        /// <summary>
        /// Nested in a larger conjunction, the split still lowers the UUID comparison, and the instant comparison
        /// lowers to a string comparison on the schema's fixed-width form.
        /// </summary>
        [Fact]
        public void EveryCostlyComparisonOfANestedPredicateLowers()
        {
            var (c, schema) = Compiled();
            var (query, q, report) = AskWithSplit(c, schema, FlaggedUpdates(c,
                c.UuidEq.Of("body.ref", DiscriminatedSchema.Ref), c.InstantGt.Of("meta.updated", DiscriminatedSchema.Since)));
            using (query)
            {
                Assert.Equal(2, report.CasesOpened);
                Assert.True(query.AreEqual(q, FlaggedUpdates(c,
                    c.StrEq.Of("body.ref", DiscriminatedSchema.Ref), c.StrGt.Of("meta.updated", DiscriminatedSchema.Since))));
                Assert.True(query.Check(c.Form.Of("body.ref", "uuid-lower")));
                var best = query.Extract(q);
                Assert.False(best.Mentions(c.UuidEq));
                Assert.False(best.Mentions(c.InstantGt));
                Assert.Equal(
                    $"(And (And (Or (StrEq \"kind\" \"a\") (StrEq \"kind\" \"b\")) (StrEq \"body.ref\" \"{DiscriminatedSchema.Ref}\")) (And (BoolTrue \"flag\") (StrGt \"meta.updated\" \"{DiscriminatedSchema.Since}\")))",
                    best.ToString());
                Assert.Equal(18, best.Cost);
            }
        }

        /// <summary>
        /// A disjunction with no discriminator of its own, modelled with a hidden one: a synthesized key
        /// <c>$anyOf/0</c>, present, whose domain is the branch indices, marked as a discriminator, with branch rules
        /// guarded on its value. The schema's split opens a case per branch, and the case join of <c>dom(status)</c>
        /// is the union of the branch domains; neither branch's own field is lifted. A query that compares status with
        /// a value outside the union is refuted; it mentions <c>status</c>, not the key, so it opens no case of its
        /// own.
        /// </summary>
        [Fact]
        public void ASplitOnAHiddenDiscriminatorJoinsTheBranchDomains()
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema);
            schema.Assume(c.Present.Of("$anyOf/0"), c.Dom.Of("$anyOf/0").Is(ValueSet.Of("0", "1")), c.Discriminator.Of("$anyOf/0"));
            c.DeclareBranch(schema, "$anyOf/0", "0", c.Present.Of("legacyId"), c.Dom.Of("status").Is(ValueSet.Of("active", "archived")));
            c.DeclareBranch(schema, "$anyOf/0", "1", c.Present.Of("id2"), c.Dom.Of("status").Is(ValueSet.Of("draft", "active")));

            var compiled = schema.Run(c.Facts);
            Assert.True(compiled.Saturated);
            Assert.Equal(2, compiled.CasesOpened);
            Assert.Empty(schema.Children);
            Assert.Equal(ValueSet.Of("active", "archived", "draft"), schema.Get(c.Dom.Of("status")));
            Assert.False(schema.Check(c.Present.Of("legacyId")));
            Assert.False(schema.Check(c.Present.Of("id2")));
            schema.Freeze();

            var (query, q, report) = AskWithSplit(c, schema, c.StrEq.Of("status", "purged"));
            using (query)
            {
                Assert.Equal(0, report.CasesOpened);
                Assert.True(query.IsContradictory);
                Assert.True(query.AreEqual(q, c.PFalse.Of()));
                var best = query.Extract(q);
                Assert.Equal("(PFalse)", best.ToString());
                Assert.Equal(0, best.Cost);
            }
        }

    }

}
