using Xunit;

namespace Eggnog.Tests.Cosmos.Literal
{

    /// <summary>
    /// <c>prototypes/cosmos-facts/04-undiscriminated-anyof.egg</c>, as written (gap 3): an undiscriminated <c>anyOf</c>
    /// becomes a hidden discriminator <c>$anyOf/0</c> over its branch indices; the split runs over the schema itself, and
    /// the lift unions the branch domains, so <c>status = 'purged'</c> contradicts the declaration. The expected figures
    /// are the egglog CLI's (vendored <c>ext/egglog</c> at 5e655cae).
    /// </summary>
    public sealed class Literal04UndiscriminatedAnyOfTests
    {

        [Fact]
        public void AnUndiscriminatedUnionDeclaresTheUnionOfItsDomains()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var root = c.Graph.Root;
            root.Assume(
                c.Present.Of(c.Schema.Of(), "$anyOf/0"),
                c.Dom.Of(c.Schema.Of(), "$anyOf/0").Is(ValueSet.Of("0", "1")),
                c.Discriminator.Of("$anyOf/0", "0"),
                c.Discriminator.Of("$anyOf/0", "1"));
            c.Horn.DefineRule("$anyOf/0 = 0", r =>
            {
                var x = r.Var<Ctx>("c");
                r.When(c.Present.Of(x, "$anyOf/0"), c.Dom.Of(x, "$anyOf/0").Is(ValueSet.Of("0")));
                r.Then(c.Present.Of(x, "legacyId"), c.Dom.Of(x, "status").Is(ValueSet.Of("active", "archived")));
            });
            c.Horn.DefineRule("$anyOf/0 = 1", r =>
            {
                var x = r.Var<Ctx>("c");
                r.When(c.Present.Of(x, "$anyOf/0"), c.Dom.Of(x, "$anyOf/0").Is(ValueSet.Of("1")));
                r.Then(c.Present.Of(x, "id2"), c.Dom.Of(x, "status").Is(ValueSet.Of("draft", "active")));
            });
            var q = root.Make(c.StrEq.Of("status", "purged"));
            root.Assume(c.WhereClause.Of(q));

            // Without the split the branches never meet anything.
            var first = root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            Assert.True(first.Saturated);
            Assert.False(root.Check(c.Dom.Of(c.Schema.Of(), "status").Is(ValueSet.Of("active", "archived", "draft"))));
            Assert.False(root.AreEqual(q, c.PFalse.Of()));

            // With it, the schema's own domain for status is the union of the branches'...
            var second = root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));
            Assert.True(second.Saturated);
            Assert.True(root.Check(c.Dom.Of(c.Schema.Of(), "status").Is(ValueSet.Of("active", "archived", "draft"))));
            Assert.Equal(ValueSet.Of("active", "archived", "draft"), root.Get(c.Dom.Of(c.Schema.Of(), "status")));

            // ...and a value outside it contradicts the declaration.
            Assert.True(root.Check(c.Contradicts.Of(c.Query.Of())));
            Assert.True(root.AreEqual(q, c.PFalse.Of()));

            // What every branch requires is not lifted: legacyId and id2 are each one branch's.
            Assert.False(root.Check(c.Present.Of(c.Schema.Of(), "legacyId")));
            Assert.False(root.Check(c.Present.Of(c.Schema.Of(), "id2")));

            PrintSize.Matches(c, "(And 0) (Assume 4) (BoolTrue 0) (InstantGt 0) (IsDefined 0) (IsString 0) (Not 0) (NumGt 0) (Or 0) (PFalse 1) (PTrue 0) (Query 1) (Schema 1) (StrEq 1) (StrGe 0) (StrGt 0) (UuidEq 0) (admits 1) (conjunct 1) (contradiction 3) (discriminator 2) (dom 12) (dom-cases 4) (dom-reported 4) (form 0) (form-cases 0) (hi 0) (inset 1) (lo 0) (ne 0) (parent 5) (present 17) (present-cases 7) (types 12) (where 1)");
            var best = root.Extract(q);
            Assert.Equal("(PFalse)", best.ToString());
            Assert.Equal(0, best.Cost);
        }

    }

}
