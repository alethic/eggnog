using Xunit;

namespace Eggnog.Tests.Cosmos.Literal
{

    /// <summary>
    /// <c>prototypes/cosmos-facts/01-discriminated-oneof.egg</c>, as written: the "B" branch of a discriminated
    /// <c>oneOf</c> declares <c>ref</c> a canonical lowercase UUID, so <c>kind = 'B'</c> lowers the UUID comparison, and
    /// without the discriminator nothing lowers. Each <c>(push)</c> block is a fresh graph; the expected figures are the
    /// egglog CLI's (vendored <c>ext/egglog</c> at 5e655cae).
    /// </summary>
    public sealed class Literal01DiscriminatedOneOfTests
    {

        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        /// <summary>
        /// The prelude plus the file's top-level rule.
        /// </summary>
        static PrototypeTheory Container()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            c.DeclareFormUnder("kind", "B", "ref", "uuid-lower");
            return c;
        }

        [Fact]
        public void ADeclaredStoredFormPutsTheComparisonInTheStatement()
        {
            var c = Container();
            var root = c.Graph.Root;
            var q = root.Make(c.And.Of(c.StrEq.Of("kind", "B"), c.UuidEq.Of("ref", Ref)));
            root.Assume(c.WhereClause.Of(q));

            var report = root.Run(Schedule.Saturate(c.Horn, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.True(root.Check(c.Form.Of(c.Query.Of(), "ref", "uuid-lower")));
            Assert.True(root.AreEqual(q, c.And.Of(c.StrEq.Of("kind", "B"), c.StrEq.Of("ref", Ref))));
            var best = root.Extract(q);
            Assert.Equal($"(And (StrEq \"kind\" \"B\") (StrEq \"ref\" \"{Ref}\"))", best.ToString());
            Assert.Equal(7, best.Cost);
        }

        [Fact]
        public void WithoutTheDiscriminatorNothingIsProvenAndNothingLowers()
        {
            var c = Container();
            var root = c.Graph.Root;
            var q = root.Make(c.UuidEq.Of("ref", Ref));
            root.Assume(c.WhereClause.Of(q));

            var report = root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.False(root.Check(c.Form.Of(c.Query.Of(), "ref", "uuid-lower")));
            var best = root.Extract(q);
            Assert.Equal($"(UuidEq \"ref\" \"{Ref}\")", best.ToString());
            Assert.Equal(1002, best.Cost);
        }

    }

}
