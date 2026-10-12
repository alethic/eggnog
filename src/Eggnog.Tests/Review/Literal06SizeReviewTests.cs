using System.Collections.Generic;
using System.Linq;
using Eggnog.Tests.Cosmos.Literal;
using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review: scenario 06 (an R1b file) through the R1a API, against egglog's figures (egglog CLI built from
    /// ext/egglog at 5e655cae).
    /// </summary>
    public sealed class Literal06SizeReviewTests
    {

        const string At = "2024-01-01T00:00:00Z";

        static Dictionary<string, int> Sizes(EGraph graph) =>
            graph.Tables.Where(t => t.Name != "contradiction").ToDictionary(t => t.Name == "contradicts" ? "contradiction" : t.Name, t => graph.Engine.TableSize(t.Function));

        static string Diff(Dictionary<string, int> expected, Dictionary<string, int> actual) =>
            string.Join("; ", expected.Where(e => actual.TryGetValue(e.Key, out var a) == false || a != e.Value)
                .Select(e => $"{e.Key}: egglog {e.Value}, eggnog {(actual.TryGetValue(e.Key, out var a) ? a : -1)}"));

        static Dictionary<string, int> Parse(string text) =>
            text.Split(')').Select(p => p.Trim().TrimStart('(').Trim()).Where(p => p.Length > 0).Select(p => p.Split(' '))
                .ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));

        [Fact]
        public void Block1()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var root = c.Graph.Root;
            root.Assume(c.Types.Of(c.Schema.Of(), "at").Is(ValueSet.Of("string")));
            var q = root.Make(c.And.Of(c.IsDefined.Of("at"), c.Or.Of(c.Not.Of(c.IsString.Of("at")), c.StrGe.Of("at", At))));
            root.Assume(c.WhereClause.Of(q));
            root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));
            Assert.True(root.AreEqual(q, c.StrGe.Of("at", At)));
            Assert.Equal(string.Empty, Diff(Parse("(And 4) (Assume 0) (IsDefined 1) (IsString 1) (Not 2) (Or 2) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 0) (StrGe 1) (admits 0) (conjunct 3) (contradiction 0) (dom 0) (form 0) (inset 0) (ne 0) (parent 1) (present 1) (types 2) (where 1)"), Sizes(c.Graph)));
            Assert.Equal($"(StrGe \"at\" \"{At}\")", root.Extract(q).ToString());
        }

        [Fact]
        public void Block2()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var root = c.Graph.Root;
            var q = root.Make(c.And.Of(c.IsDefined.Of("at"), c.Or.Of(c.Not.Of(c.IsString.Of("at")), c.StrGe.Of("at", At))));
            root.Assume(c.WhereClause.Of(q));
            root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));
            Assert.False(root.AreEqual(q, c.StrGe.Of("at", At)));
            Assert.Equal(string.Empty, Diff(Parse("(And 2) (Assume 0) (IsDefined 1) (IsString 1) (Not 1) (Or 2) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 0) (StrGe 1) (admits 0) (conjunct 3) (contradiction 0) (dom 0) (form 0) (inset 0) (ne 0) (parent 1) (present 0) (types 0) (where 1)"), Sizes(c.Graph)));
            Assert.Equal($"(And (IsDefined \"at\") (Or (Not (IsString \"at\")) (StrGe \"at\" \"{At}\")))", root.Extract(q).ToString());
        }

        [Fact]
        public void Block3()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var root = c.Graph.Root;
            root.Assume(c.Present.Of(c.Schema.Of(), "kind"), c.Dom.Of(c.Schema.Of(), "kind").Is(ValueSet.Of("A")));
            var q = root.Make(c.And.Of(c.StrEq.Of("kind", "A"), c.StrEq.Of("id", "x")));
            root.Assume(c.WhereClause.Of(q));
            root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));
            Assert.True(root.AreEqual(q, c.StrEq.Of("id", "x")));
            Assert.Equal(string.Empty, Diff(Parse("(And 2) (Assume 0) (IsDefined 0) (IsString 0) (Not 0) (Or 0) (PFalse 0) (PTrue 1) (Query 1) (Schema 1) (StrEq 2) (StrGe 0) (admits 3) (conjunct 2) (contradiction 0) (dom 3) (form 0) (inset 2) (ne 0) (parent 1) (present 3) (types 3) (where 1)"), Sizes(c.Graph)));
            Assert.Equal("(StrEq \"id\" \"x\")", root.Extract(q).ToString());
        }

        [Fact]
        public void Block4()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var root = c.Graph.Root;
            root.Assume(c.Dom.Of(c.Schema.Of(), "kind").Is(ValueSet.Of("A")));
            var q = root.Make(c.And.Of(c.StrEq.Of("kind", "A"), c.StrEq.Of("id", "x")));
            root.Assume(c.WhereClause.Of(q));
            root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));
            Assert.False(root.AreEqual(q, c.StrEq.Of("id", "x")));
            Assert.Equal(string.Empty, Diff(Parse("(And 2) (Assume 0) (IsDefined 0) (IsString 0) (Not 0) (Or 0) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 2) (StrGe 0) (admits 4) (conjunct 3) (contradiction 0) (dom 3) (form 0) (inset 2) (ne 0) (parent 1) (present 2) (types 3) (where 1)"), Sizes(c.Graph)));
            Assert.Equal("(And (StrEq \"kind\" \"A\") (StrEq \"id\" \"x\"))", root.Extract(q).ToString());
        }

    }

}
