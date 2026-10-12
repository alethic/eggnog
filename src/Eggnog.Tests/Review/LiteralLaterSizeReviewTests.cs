using System.Collections.Generic;
using System.Linq;
using Eggnog.Tests.Cosmos.Literal;
using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review: scenarios 04 and 05 (R1b files) run through the R1a API, against egglog's figures (egglog CLI built
    /// from ext/egglog at 5e655cae): a wider cross-check of lowering, lattices, contradiction and extraction.
    /// </summary>
    public sealed class LiteralLaterSizeReviewTests
    {

        static Dictionary<string, int> Sizes(EGraph graph) =>
            graph.Tables.Where(t => t.Name != "contradiction").ToDictionary(t => t.Name == "contradicts" ? "contradiction" : t.Name, t => graph.Engine.TableSize(t.Function));

        static string Diff(Dictionary<string, int> expected, Dictionary<string, int> actual) =>
            string.Join("; ", expected.Where(e => actual.TryGetValue(e.Key, out var a) == false || a != e.Value)
                .Select(e => $"{e.Key}: egglog {e.Value}, eggnog {(actual.TryGetValue(e.Key, out var a) ? a : -1)}"));

        static Dictionary<string, int> Parse(string text) =>
            text.Split(')').Select(p => p.Trim().TrimStart('(').Trim()).Where(p => p.Length > 0).Select(p => p.Split(' '))
                .ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));

        [Fact]
        public void Literal04MatchesEgglog()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var root = c.Graph.Root;
            root.Assume(
                c.Present.Of(c.Schema.Of(), "$anyOf/0"),
                c.Dom.Of(c.Schema.Of(), "$anyOf/0").Is(ValueSet.Of("0", "1")),
                c.Discriminator.Of("$anyOf/0", "0"),
                c.Discriminator.Of("$anyOf/0", "1"));
            c.Horn.DefineRule("branch 0", r =>
            {
                var x = r.Var<Ctx>("c");
                r.When(c.Present.Of(x, "$anyOf/0"), c.Dom.Of(x, "$anyOf/0").Is(ValueSet.Of("0")));
                r.Then(c.Present.Of(x, "legacyId"), c.Dom.Of(x, "status").Is(ValueSet.Of("active", "archived")));
            });
            c.Horn.DefineRule("branch 1", r =>
            {
                var x = r.Var<Ctx>("c");
                r.When(c.Present.Of(x, "$anyOf/0"), c.Dom.Of(x, "$anyOf/0").Is(ValueSet.Of("1")));
                r.Then(c.Present.Of(x, "id2"), c.Dom.Of(x, "status").Is(ValueSet.Of("draft", "active")));
            });
            var q = root.Make(c.StrEq.Of("status", "purged"));
            root.Assume(c.WhereClause.Of(q));
            root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            Assert.False(root.Check(c.Dom.Of(c.Schema.Of(), "status").Is(ValueSet.Of("active", "archived", "draft"))));
            Assert.False(root.AreEqual(q, c.PFalse.Of()));
            root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));
            Assert.True(root.Check(c.Dom.Of(c.Schema.Of(), "status").Is(ValueSet.Of("active", "archived", "draft"))));
            Assert.True(root.Check(c.Contradicts.Of(c.Query.Of())));
            Assert.True(root.AreEqual(q, c.PFalse.Of()));
            Assert.False(root.Check(c.Present.Of(c.Schema.Of(), "legacyId")));
            Assert.False(root.Check(c.Present.Of(c.Schema.Of(), "id2")));

            var expected = Parse("(And 0) (Assume 4) (Not 0) (Or 0) (PFalse 1) (PTrue 0) (Query 1) (Schema 1) (StrEq 1) (UuidEq 0) (admits 1) (conjunct 1) (contradiction 3) (discriminator 2) (dom 12) (dom-cases 4) (dom-reported 4) (form 0) (form-cases 0) (hi 0) (inset 1) (lo 0) (ne 0) (parent 5) (present 17) (present-cases 7) (types 12) (where 1)");
            Assert.Equal(string.Empty, Diff(expected, Sizes(c.Graph)));
            var best = root.Extract(q);
            Assert.Equal("(PFalse)", best.ToString());
            Assert.Equal(0, best.Cost);
        }

        [Theory]
        [InlineData(500L, true, "(PFalse)", "(And 0) (Assume 0) (NumGt 1) (PFalse 1) (PTrue 0) (Query 1) (Schema 1) (conjunct 1) (contradiction 1) (dom 0) (hi 2) (lo 2) (parent 1) (present 1) (types 2) (where 1)")]
        [InlineData(50L, false, "(NumGt \"n\" 50)", "(And 0) (Assume 0) (NumGt 1) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (conjunct 1) (contradiction 0) (dom 0) (hi 2) (lo 2) (parent 1) (present 1) (types 2) (where 1)")]
        public void Literal05MatchesEgglog(long bound, bool contradicted, string extracted, string sizes)
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var root = c.Graph.Root;
            root.Assume(
                c.Types.Of(c.Schema.Of(), "n").Is(ValueSet.Of("number")),
                c.Lo.Of(c.Schema.Of(), "n").Is(0L),
                c.Hi.Of(c.Schema.Of(), "n").Is(100L));
            var q = root.Make(c.NumGt.Of("n", bound));
            root.Assume(c.WhereClause.Of(q));
            root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));
            Assert.Equal(contradicted, root.Check(c.Contradicts.Of(c.Query.Of())));
            if (contradicted == false)
                Assert.Equal(51L, root.Get(c.Lo.Of(c.Query.Of(), "n")));

            Assert.Equal(string.Empty, Diff(Parse(sizes), Sizes(c.Graph)));
            Assert.Equal(extracted, root.Extract(q).ToString());
        }

    }

}
