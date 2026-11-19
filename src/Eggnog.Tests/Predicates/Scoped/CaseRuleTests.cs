using System;

using Xunit;

namespace Eggnog.Tests.Predicates.Scoped
{

    /// <summary>
    /// <see cref="PredicateTheory"/>'s case rules on small schemas: the values of a discriminator they drop, because the
    /// scope rules out what their branches declare, and what they derive from the branches of the values left: stored
    /// forms, a string type declared by different claims, and the union of the declared domains over those values
    /// only. Each schema is compiled with <see cref="PredicateTheory.Facts"/> and each query run with
    /// <see cref="PredicateTheory.Saturation"/>.
    /// </summary>
    public sealed class CaseRuleTests
    {

        /// <summary>
        /// Compiles a schema under a new graph's frozen root with <see cref="PredicateTheory.Facts"/> and freezes it.
        /// </summary>
        static (PredicateTheory C, Scope Schema) Compile(Action<PredicateTheory, Scope> declare)
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema);
            declare(c, schema);
            Assert.True(schema.Run(c.Facts).Saturated);
            schema.Freeze();
            return (c, schema);
        }

        /// <summary>
        /// Asks a query with the query's case rules and checks that the run saturated.
        /// </summary>
        static (Scope Query, Pred Q) Ask(PredicateTheory c, Scope schema, ConstructorTerm<Pred> term)
        {
            var query = schema.CreateChild(c.Query);
            var q = query.Make(term);
            query.Assume(c.WhereClause.Of(q));
            Assert.True(query.Run(c.Saturation).Saturated);
            return (query, q);
        }

        /// <summary>
        /// <c>kind</c> holds a, b or c; the branches of a and b declare the stored form of <c>ref</c>, and c's that of
        /// <c>at</c>.
        /// </summary>
        static (PredicateTheory C, Scope Schema) Forms() => Compile((c, s) =>
        {
            s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of(["a", "b", "c"])), c.Discriminator.Of("kind"));
            c.DeclareBranch(s, "kind", "a", forms: [("ref", "uuid-lower")]);
            c.DeclareBranch(s, "kind", "b", forms: [("ref", "uuid-lower")]);
            c.DeclareBranch(s, "kind", "c", forms: [("at", "instant-ms")]);
        });

        /// <summary>
        /// <c>kind</c> holds a, b or c, and their branches declare <c>status</c>'s domain as x, y and z.
        /// </summary>
        static (PredicateTheory C, Scope Schema) Domains() => Compile((c, s) =>
        {
            s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of(["a", "b", "c"])), c.Discriminator.Of("kind"));
            c.DeclareBranch(s, "kind", "a", domains: [("status", Set.Of(["x"]))]);
            c.DeclareBranch(s, "kind", "b", domains: [("status", Set.Of(["y"]))]);
            c.DeclareBranch(s, "kind", "c", domains: [("status", Set.Of(["z"]))]);
        });

        /// <summary>
        /// The discriminated schema: a number at <c>body.ref</c>, whose stored form the branches of a and b declare,
        /// rules out both, so <c>kind</c> holds c, and c's branch makes <c>body.pos</c> present.
        /// </summary>
        [Fact]
        public void ACaseWhoseStoredFormThePathsTypesRuleOutIsDropped()
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = DiscriminatedSchema.Compile(c);
            var (query, q) = Ask(c, schema, c.And.Of(c.NumGt.Of("body.ref", 0L), c.StrGe.Of("kind", "a")));
            using (query)
            {
                Assert.False(query.IsContradictory);
                Assert.Equal(Set.Of(["c"]), query.Get(c.Dom.Of("kind")));
                Assert.True(query.Check(c.Present.Of("body.pos")));
                var best = query.Extract(q);
                Assert.Equal("(And (NumGt \"body.ref\" 0) (StrGe \"kind\" \"a\"))", best.ToString());
                Assert.Equal<ulong>(7, best.Cost);
            }
        }

        /// <summary>
        /// Numbers at <c>ref</c> and at <c>at</c> rule out every value of <c>kind</c>, so no document satisfies the
        /// query, and the predicate is false.
        /// </summary>
        [Fact]
        public void AQueryThatRulesOutEveryCaseIsRefuted()
        {
            var (c, schema) = Forms();
            var (query, q) = Ask(c, schema, c.And.Of(c.And.Of(c.NumGt.Of("ref", 0L), c.NumGt.Of("at", 0L)), c.Not.Of(c.StrEq.Of("kind", "zz"))));
            using (query)
            {
                Assert.True(query.IsContradictory);
                var best = query.Extract(q);
                Assert.Equal("(PFalse)", best.ToString());
                Assert.Equal<ulong>(0, best.Cost);
            }
        }

        /// <summary>
        /// A number at <c>ref</c> leaves c alone, whose branch rule declares the form of <c>at</c>, so the instant
        /// comparison lowers; a number at <c>at</c> leaves a and b, which both declare the form of <c>ref</c>, so the
        /// UUID comparison lowers.
        /// </summary>
        [Fact]
        public void TheCasesLeftLowerWhatTheirBranchesDeclare()
        {
            var (c, schema) = Forms();
            var (query, q) = Ask(c, schema, c.And.Of(c.And.Of(c.NumGt.Of("ref", 0L), c.InstantGt.Of("at", DiscriminatedSchema.Since)), c.Not.Of(c.StrEq.Of("kind", "zz"))));
            using (query)
            {
                Assert.Equal(Set.Of(["c"]), query.Get(c.Dom.Of("kind")));
                Assert.True(query.Check(c.Form.Of("at", "instant-ms")));
                var best = query.Extract(q);
                Assert.Equal($"(And (And (NumGt \"ref\" 0) (StrGt \"at\" \"{DiscriminatedSchema.Since}\")) (Not (StrEq \"kind\" \"zz\")))", best.ToString());
                Assert.Equal<ulong>(12, best.Cost);
            }

            var (other, p) = Ask(c, schema, c.And.Of(c.And.Of(c.NumGt.Of("at", 0L), c.UuidEq.Of("ref", DiscriminatedSchema.Ref)), c.Not.Of(c.StrEq.Of("kind", "zz"))));
            using (other)
            {
                Assert.Equal(Set.Of(["a", "b"]), other.Get(c.Dom.Of("kind")));
                Assert.True(other.Check(c.Form.Of("ref", "uuid-lower")));
                var best = other.Extract(p);
                Assert.Equal($"(And (And (NumGt \"at\" 0) (StrEq \"ref\" \"{DiscriminatedSchema.Ref}\")) (Not (StrEq \"kind\" \"zz\")))", best.ToString());
                Assert.Equal<ulong>(12, best.Cost);
            }
        }

        /// <summary>
        /// The hidden discriminator of <see cref="DiscriminatedSchemaTests"/>: a query for archived documents leaves
        /// branch 1's declared domain of <c>status</c> empty, so <c>$anyOf/0</c> holds 0, whose branch makes
        /// <c>legacyId</c> present.
        /// </summary>
        [Fact]
        public void ACaseDomainThatTheQueryLeavesEmptyIsDropped()
        {
            var (c, schema) = Compile((c, s) =>
            {
                s.Assume(c.Present.Of("$anyOf/0"), c.Dom.Of("$anyOf/0").Is(Set.Of(["0", "1"])), c.Discriminator.Of("$anyOf/0"));
                c.DeclareBranch(s, "$anyOf/0", "0", present: ["legacyId"], domains: [("status", Set.Of(["active", "archived"]))]);
                c.DeclareBranch(s, "$anyOf/0", "1", present: ["id2"], domains: [("status", Set.Of(["draft", "active"]))]);
            });
            var (query, _) = Ask(c, schema, c.And.Of(c.StrEq.Of("status", "archived"), c.IsDefined.Of("$anyOf/0")));
            using (query)
            {
                Assert.False(query.IsContradictory);
                Assert.Equal(Set.Of(["0"]), query.Get(c.Dom.Of("$anyOf/0")));
                Assert.True(query.Check(c.Present.Of("legacyId")));
                Assert.False(query.Check(c.Present.Of("id2")));
            }
        }

        /// <summary>
        /// A refused value empties the domain of <c>status</c> that a's branch declares, though no domain of
        /// <c>status</c> holds in the scope, so of a and b only b is left.
        /// </summary>
        [Fact]
        public void ARefusedValueEmptiesACaseDomain()
        {
            var (c, schema) = Compile((c, s) =>
            {
                s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of(["a", "b", "c"])), c.Discriminator.Of("kind"));
                c.DeclareBranch(s, "kind", "a", domains: [("status", Set.Of(["x"]))]);
            });
            var (query, _) = Ask(c, schema, c.And.Of(c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")), c.Not.Of(c.StrEq.Of("status", "x"))));
            using (query)
            {
                Assert.False(query.IsContradictory);
                Assert.Equal(Set.Of(["b"]), query.Get(c.Dom.Of("kind")));
            }
        }

        /// <summary>
        /// What a schema's branches declare present is checked against the schema's own facts: a branch that declares a
        /// path present and a stored form of it that the schema types as a number, or a domain of it that the schema's
        /// domain excludes, has no document, so the schema drops its value; a branch that declares the same but not the
        /// path's presence keeps it, since the path may be absent there.
        /// </summary>
        [Fact]
        public void ACaseWhoseDeclaredPresentPathTheSchemaRulesOutIsDropped()
        {
            var (c, schema) = Compile((c, s) =>
            {
                s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of(["a", "b", "c"])), c.Discriminator.Of("kind"), c.Types.Of("n").Is(Set.Of(["number"])));
                c.DeclareBranch(s, "kind", "a", present: ["n"], forms: [("n", "uuid-lower")]);
                c.DeclareBranch(s, "kind", "c", forms: [("n", "uuid-lower")]);
            });
            Assert.Equal(Set.Of(["b", "c"]), schema.Get(c.Dom.Of("kind")));
            Assert.False(schema.IsContradictory);

            (c, schema) = Compile((c, s) =>
            {
                s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of(["a", "b", "c"])), c.Discriminator.Of("kind"), c.Dom.Of("status").Is(Set.Of(["x"])));
                c.DeclareBranch(s, "kind", "a", present: ["status"], domains: [("status", Set.Of(["y"]))]);
                c.DeclareBranch(s, "kind", "c", domains: [("status", Set.Of(["y"]))]);
            });
            Assert.Equal(Set.Of(["b", "c"]), schema.Get(c.Dom.Of("kind")));
            Assert.False(schema.IsContradictory);
        }

        /// <summary>
        /// The domain of <c>status</c> that the case rules derive is the union of the domains declared by the values
        /// left: the schema's, over a, b and c, is x, y and z; a query of kinds a and b narrows it to x and y, so it
        /// refutes a status of z.
        /// </summary>
        [Fact]
        public void TheUnionOfTheCaseDomainsIsOverTheValuesLeft()
        {
            var (c, schema) = Domains();
            Assert.Equal(Set.Of(["x", "y", "z"]), schema.Get(c.Dom.Of("status")));

            var (query, _) = Ask(c, schema, c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")));
            using (query)
                Assert.Equal(Set.Of(["x", "y"]), query.Get(c.Dom.Of("status")));

            var (refuted, q) = Ask(c, schema, c.And.Of(c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")), c.StrEq.Of("status", "z")));
            using (refuted)
            {
                Assert.True(refuted.IsContradictory);
                Assert.Equal("(PFalse)", refuted.Extract(q).ToString());
            }
        }

        /// <summary>
        /// A schema's own domain of its discriminator bounds the union too: <c>kind</c> holds a or b, so the branch of
        /// c, which declares z, adds nothing, and a status of z is refuted.
        /// </summary>
        [Fact]
        public void TheSchemasUnionIsOverItsOwnDomain()
        {
            var (c, schema) = Compile((c, s) =>
            {
                s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of(["a", "b"])), c.Discriminator.Of("kind"));
                c.DeclareBranch(s, "kind", "a", domains: [("status", Set.Of(["x"]))]);
                c.DeclareBranch(s, "kind", "b", domains: [("status", Set.Of(["y"]))]);
                c.DeclareBranch(s, "kind", "c", domains: [("status", Set.Of(["z"]))]);
            });
            Assert.Equal(Set.Of(["x", "y"]), schema.Get(c.Dom.Of("status")));

            var (query, _) = Ask(c, schema, c.StrEq.Of("status", "z"));
            using (query)
                Assert.True(query.IsContradictory);
        }

        /// <summary>
        /// A union narrowed to one value selects that value's branch of another discriminator: kinds a and b both
        /// declare a status of x, whose branch declares the form of <c>ref</c>, so the UUID comparison lowers.
        /// </summary>
        [Fact]
        public void ANarrowedUnionSelectsTheBranchOfAnotherDiscriminator()
        {
            var (c, schema) = Compile((c, s) =>
            {
                s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of(["a", "b", "c"])), c.Discriminator.Of("kind"));
                s.Assume(c.Present.Of("status"), c.Dom.Of("status").Is(Set.Of(["x", "y"])), c.Discriminator.Of("status"));
                c.DeclareBranch(s, "kind", "a", domains: [("status", Set.Of(["x"]))]);
                c.DeclareBranch(s, "kind", "b", domains: [("status", Set.Of(["x"]))]);
                c.DeclareBranch(s, "kind", "c", domains: [("status", Set.Of(["y"]))]);
                c.DeclareBranch(s, "status", "x", forms: [("ref", "uuid-lower")]);
            });
            var (query, q) = Ask(c, schema, c.And.Of(c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")), c.UuidEq.Of("ref", DiscriminatedSchema.Ref)));
            using (query)
            {
                Assert.Equal(Set.Of(["x"]), query.Get(c.Dom.Of("status")));
                Assert.True(query.Check(c.Form.Of("ref", "uuid-lower")));
                var best = query.Extract(q);
                Assert.Equal($"(And (Or (StrEq \"kind\" \"a\") (StrEq \"kind\" \"b\")) (StrEq \"ref\" \"{DiscriminatedSchema.Ref}\"))", best.ToString());
                Assert.Equal<ulong>(11, best.Cost);
            }
        }

        /// <summary>
        /// Branches that declare one path's strings by different claims, a stored form and a domain, give it a string
        /// or null in the schema: a negated string test of it becomes a negated presence test, which the schema's
        /// declared presence makes false, and a number at it is refuted.
        /// </summary>
        [Fact]
        public void CasesThatDeclareStringsByDifferentClaimsTypeThePath()
        {
            var (c, schema) = Compile((c, s) =>
            {
                s.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of(["a", "b"])), c.Discriminator.Of("kind"));
                c.DeclareBranch(s, "kind", "a", present: ["x"], forms: [("x", "uuid-lower")]);
                c.DeclareBranch(s, "kind", "b", present: ["x"], domains: [("x", Set.Of(["u", "v"]))]);
            });
            Assert.Equal(Set.Of(["null", "string"]), schema.Get(c.Types.Of("x")));

            var (query, q) = Ask(c, schema, c.And.Of(c.Not.Of(c.IsString.Of("x")), c.StrGe.Of("kind", "a")));
            using (query)
            {
                var best = query.Extract(q);
                Assert.Equal("(And (Not (PTrue)) (StrGe \"kind\" \"a\"))", best.ToString());
                Assert.Equal<ulong>(5, best.Cost);
            }

            var (refuted, _) = Ask(c, schema, c.And.Of(c.NumGt.Of("x", 0L), c.StrGe.Of("kind", "a")));
            using (refuted)
                Assert.True(refuted.IsContradictory);
        }

    }

}
