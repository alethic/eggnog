using Xunit;

namespace Eggnog.Tests.Cosmos.Literal
{

    /// <summary>
    /// <c>prototypes/cosmos-facts/06-guard-deletion.egg</c>, as written: guard deletion and tautologies as rewrites plus
    /// extraction. A declared type makes a weakened ordering comparison exact; a required const deletes its tautology; a
    /// const that is not required does not, because tautologies are judged in <c>(Schema)</c>, never on the query's own
    /// conjuncts. Each <c>(push)</c> block is a fresh graph; the expected figures are the egglog CLI's (vendored
    /// <c>ext/egglog</c> at 5e655cae).
    /// </summary>
    public sealed class Literal06GuardDeletionTests
    {

        const string At = "2024-01-01T00:00:00Z";

        /// <summary>
        /// <c>IS_DEFINED(at) AND (NOT IS_STRING(at) OR at &gt;= '2024-01-01T00:00:00Z')</c>, the adapter's weakened
        /// ordering comparison, asserted as the query's WHERE clause.
        /// </summary>
        static Pred Weakened(PrototypeTheory c)
        {
            var root = c.Graph.Root;
            var q = root.Make(c.And.Of(c.IsDefined.Of("at"), c.Or.Of(c.Not.Of(c.IsString.Of("at")), c.StrGe.Of("at", At))));
            root.Assume(c.WhereClause.Of(q));
            return q;
        }

        /// <summary>
        /// <c>kind = 'A' AND id = 'x'</c>, asserted as the query's WHERE clause.
        /// </summary>
        static Pred KindAndId(PrototypeTheory c)
        {
            var root = c.Graph.Root;
            var q = root.Make(c.And.Of(c.StrEq.Of("kind", "A"), c.StrEq.Of("id", "x")));
            root.Assume(c.WhereClause.Of(q));
            return q;
        }

        /// <summary>
        /// The prelude, grounded.
        /// </summary>
        static PrototypeTheory Prelude()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            return c;
        }

        /// <summary>
        /// The schedule every block runs: <c>(run-schedule (saturate horn lattice split rewrite))</c>.
        /// </summary>
        static RunReport Run(PrototypeTheory c) => c.Graph.Root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));

        [Fact]
        public void ADeclaredTypeMakesAnOrderingComparisonExact()
        {
            var c = Prelude();
            var root = c.Graph.Root;
            root.Assume(c.Types.Of(c.Schema.Of(), "at").Is(ValueSet.Of("string")));
            var q = Weakened(c);

            Assert.True(Run(c).Saturated);

            Assert.True(root.AreEqual(q, c.StrGe.Of("at", At)));
            PrintSize.Matches(c, "(And 4) (Assume 0) (BoolTrue 0) (InstantGt 0) (IsDefined 1) (IsString 1) (Not 2) (NumGt 0) (Or 2) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 0) (StrGe 1) (StrGt 0) (UuidEq 0) (admits 0) (conjunct 3) (contradiction 0) (discriminator 0) (dom 0) (dom-cases 0) (dom-reported 0) (form 0) (form-cases 0) (hi 0) (inset 0) (lo 0) (ne 0) (parent 1) (present 1) (present-cases 0) (types 2) (where 1)");
            var best = root.Extract(q);
            Assert.Equal($"(StrGe \"at\" \"{At}\")", best.ToString());
            Assert.Equal(3, best.Cost);
        }

        [Fact]
        public void WithoutADeclaredTypeTheComparisonIsStillWeakened()
        {
            var c = Prelude();
            var root = c.Graph.Root;
            var q = Weakened(c);

            Assert.True(Run(c).Saturated);

            Assert.False(root.AreEqual(q, c.StrGe.Of("at", At)));
            PrintSize.Matches(c, "(And 2) (Assume 0) (BoolTrue 0) (InstantGt 0) (IsDefined 1) (IsString 1) (Not 1) (NumGt 0) (Or 2) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 0) (StrGe 1) (StrGt 0) (UuidEq 0) (admits 0) (conjunct 3) (contradiction 0) (discriminator 0) (dom 0) (dom-cases 0) (dom-reported 0) (form 0) (form-cases 0) (hi 0) (inset 0) (lo 0) (ne 0) (parent 1) (present 0) (present-cases 0) (types 0) (where 1)");
            var best = root.Extract(q);
            Assert.Equal($"(And (IsDefined \"at\") (Or (Not (IsString \"at\")) (StrGe \"at\" \"{At}\")))", best.ToString());
            Assert.Equal(10, best.Cost);
        }

        [Fact]
        public void ADeclaredTautologyIsNotAsked()
        {
            var c = Prelude();
            var root = c.Graph.Root;
            root.Assume(c.Present.Of(c.Schema.Of(), "kind"), c.Dom.Of(c.Schema.Of(), "kind").Is(ValueSet.Of("A")));
            var q = KindAndId(c);

            Assert.True(Run(c).Saturated);

            Assert.True(root.AreEqual(q, c.StrEq.Of("id", "x")));
            PrintSize.Matches(c, "(And 2) (Assume 0) (BoolTrue 0) (InstantGt 0) (IsDefined 0) (IsString 0) (Not 0) (NumGt 0) (Or 0) (PFalse 0) (PTrue 1) (Query 1) (Schema 1) (StrEq 2) (StrGe 0) (StrGt 0) (UuidEq 0) (admits 3) (conjunct 2) (contradiction 0) (discriminator 0) (dom 3) (dom-cases 0) (dom-reported 0) (form 0) (form-cases 0) (hi 0) (inset 2) (lo 0) (ne 0) (parent 1) (present 3) (present-cases 0) (types 3) (where 1)");
            var best = root.Extract(q);
            Assert.Equal("(StrEq \"id\" \"x\")", best.ToString());
            Assert.Equal(3, best.Cost);
        }

        [Fact]
        public void WithoutAPresenceClaimTheComparisonStays()
        {
            var c = Prelude();
            var root = c.Graph.Root;
            root.Assume(c.Dom.Of(c.Schema.Of(), "kind").Is(ValueSet.Of("A")));
            var q = KindAndId(c);

            Assert.True(Run(c).Saturated);

            // The query's own conjunct proves presence in (Query), and that is not what deletes it.
            Assert.False(root.AreEqual(q, c.StrEq.Of("id", "x")));
            Assert.True(root.Check(c.Present.Of(c.Query.Of(), "kind")));
            Assert.False(root.Check(c.Present.Of(c.Schema.Of(), "kind")));
            PrintSize.Matches(c, "(And 2) (Assume 0) (BoolTrue 0) (InstantGt 0) (IsDefined 0) (IsString 0) (Not 0) (NumGt 0) (Or 0) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 2) (StrGe 0) (StrGt 0) (UuidEq 0) (admits 4) (conjunct 3) (contradiction 0) (discriminator 0) (dom 3) (dom-cases 0) (dom-reported 0) (form 0) (form-cases 0) (hi 0) (inset 2) (lo 0) (ne 0) (parent 1) (present 2) (present-cases 0) (types 3) (where 1)");
            var best = root.Extract(q);
            Assert.Equal("(And (StrEq \"kind\" \"A\") (StrEq \"id\" \"x\"))", best.ToString());
            Assert.Equal(7, best.Cost);
        }

    }

}
