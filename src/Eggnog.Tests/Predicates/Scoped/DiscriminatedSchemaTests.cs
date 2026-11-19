using System;
using System.Collections.Generic;
using System.Linq;

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
        /// Freezes the theory's graph's empty root and compiles a schema of <paramref name="claims"/> alone under it:
        /// horn and lattice rules, then the copies of its facts (<see cref="PredicateTheory.PlainFacts"/>); then
        /// freezes it.
        /// </summary>
        static Scope SchemaOf(PredicateTheory c, params ReadOnlySpan<Fact> claims)
        {
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild();
            if (claims.Length > 0)
                schema.Assume(claims);

            Assert.True(schema.Run(c.PlainFacts).Saturated);
            schema.Freeze();
            return schema;
        }

        /// <summary>
        /// Asks a query with the query's case rules (<see cref="PredicateTheory.Saturation"/>) and checks that the run
        /// saturated and left no child behind.
        /// </summary>
        static (Scope Query, Pred Q) AskWithCases(PredicateTheory c, Scope schema, ConstructorTerm<Pred> term)
        {
            var query = schema.CreateChild();
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
        /// No rule of a query's schedule, its case rules included, writes the copies of the schema's facts
        /// (<see cref="PredicateTheory.SchemaPresent"/>, <see cref="PredicateTheory.SchemaDom"/>) that the rewrites
        /// deleting a conjunct read: after the query's run they hold exactly the schema's presences and domains.
        /// <c>StrEq(type, Record)</c> gives the type the domain {Record} in its query; the schema declares the type
        /// present but no domain of it, so the copies hold none, and the conjunct stays.
        /// </summary>
        [Fact]
        public void AQuerysRunWritesNoCopyOfItsSchemasFacts()
        {
            static IEnumerable<string> Rendered(Scope scope, TableHandle table) =>
                scope.Rows(table).Select(row => string.Join(" ", row.Keys) + " = " + row.Output);

            var (c, schema) = Compiled();
            var (query, q) = AskWithCases(c, schema, c.StrEq.Of("type", "Record"));
            using (query)
            {
                Assert.True(query.Check(c.Present.Of("type")));
                Assert.Equal(Set.Of(["Record"]), query.Get(c.Dom.Of("type")));
                Assert.False(query.TryGet(c.SchemaDom.Of("type"), out _));
                Assert.Equal(Rendered(schema, c.Present), Rendered(query, c.SchemaPresent));
                Assert.Equal(Rendered(schema, c.Dom), Rendered(query, c.SchemaDom));
                Assert.Equal("(StrEq \"type\" \"Record\")", query.Extract(q).ToString());
            }
        }

        /// <summary>
        /// A definedness test is deleted on the schema's presence claims alone (declared presence): the query's own
        /// bound makes <c>n</c> present in the query either way, and the test goes only where the schema declares
        /// <c>n</c> present.
        /// </summary>
        /// <param name="declared">Whether the schema declares <c>n</c> present.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ADefinednessTestIsDeletedOnlyWhereTheSchemaDeclaresThePathPresent(bool declared)
        {
            var c = new PredicateTheory(new EGraph());
            var schema = declared ? SchemaOf(c, c.Present.Of("n")) : SchemaOf(c);
            var (query, q) = DiscriminatedSchema.Ask(c, schema, c.And.Of(c.IsDefined.Of("n"), c.NumGt.Of("n", 5L)));
            using (query)
            {
                Assert.True(query.Check(c.Present.Of("n")));
                Assert.Equal(declared, query.Check(c.SchemaPresent.Of("n")));
                var best = query.Extract(q);
                Assert.Equal(declared ? "(NumGt \"n\" 5)" : "(And (IsDefined \"n\") (NumGt \"n\" 5))", best.ToString());
                Assert.Equal(declared ? 3UL : 6UL, best.Cost);
            }
        }

        /// <summary>
        /// A comparison its path's domain implies is deleted only where the schema also declares the path present
        /// (declared tautology): the query's own comparison makes <c>kind</c> present in the query either way.
        /// </summary>
        /// <param name="declared">Whether the schema declares <c>kind</c> present.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AComparisonIsDeletedOnlyWhereTheSchemaDeclaresThePathPresent(bool declared)
        {
            var c = new PredicateTheory(new EGraph());
            var domain = c.Dom.Of("kind").Is(Set.Of(["A"]));
            var schema = declared ? SchemaOf(c, domain, c.Present.Of("kind")) : SchemaOf(c, domain);
            var (query, q) = DiscriminatedSchema.Ask(c, schema, c.And.Of(c.StrEq.Of("kind", "A"), c.StrEq.Of("id", "x")));
            using (query)
            {
                Assert.True(query.Check(c.Present.Of("kind")));
                Assert.Equal(declared, query.Check(c.SchemaPresent.Of("kind")));
                Assert.Equal(Set.Of(["A"]), query.Get(c.SchemaDom.Of("kind")));
                var best = query.Extract(q);
                Assert.Equal(declared ? "(StrEq \"id\" \"x\")" : "(And (StrEq \"kind\" \"A\") (StrEq \"id\" \"x\"))", best.ToString());
                Assert.Equal(declared ? 3UL : 7UL, best.Cost);
            }
        }

        /// <summary>
        /// A comparison is deleted only where the schema's own domain of its path implies it (declared tautology): the
        /// query's own comparison gives <c>ref</c> the domain {r1} in the query either way.
        /// </summary>
        /// <param name="declared">Whether the schema declares the domain {r1} of <c>ref</c>.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AComparisonIsDeletedOnlyWhereTheSchemasDomainImpliesIt(bool declared)
        {
            var c = new PredicateTheory(new EGraph());
            var present = c.Present.Of("ref");
            var schema = declared ? SchemaOf(c, present, c.Dom.Of("ref").Is(Set.Of(["r1"]))) : SchemaOf(c, present);
            var (query, q) = DiscriminatedSchema.Ask(c, schema, c.And.Of(c.StrEq.Of("ref", "r1"), c.StrEq.Of("id", "x")));
            using (query)
            {
                Assert.Equal(Set.Of(["r1"]), query.Get(c.Dom.Of("ref")));
                Assert.Equal(declared, query.TryGet(c.SchemaDom.Of("ref"), out _));
                var best = query.Extract(q);
                Assert.Equal(declared ? "(StrEq \"id\" \"x\")" : "(And (StrEq \"ref\" \"r1\") (StrEq \"id\" \"x\"))", best.ToString());
                Assert.Equal(declared ? 3UL : 7UL, best.Cost);
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
            var schema = c.Graph.Root.CreateChild();
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
