using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Cosmos.Literal
{

    /// <summary>
    /// The table sizes of the literal scenarios 01 to 03 after their runs, against egglog's <c>(print-size)</c> at the
    /// point of the scenario's <c>(extract $q)</c> (egglog CLI built from ext/egglog at 5e655cae).
    /// </summary>
    public sealed class Literal01To03SizeTests
    {

        /// <summary>
        /// The reference the queries compare a UUID path with.
        /// </summary>
        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        /// <summary>
        /// The size of each table of the graph, keyed by its egglog name: the graph's own contradiction relation is
        /// left out, and the theory's <c>contradicts</c> is the prelude's <c>contradiction</c>.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <returns>The sizes, by table name.</returns>
        static Dictionary<string, int> Sizes(EGraph graph) =>
            graph.Tables.Where(t => t.Name != "contradiction").ToDictionary(t => t.Name == "contradicts" ? "contradiction" : t.Name, t => graph.Engine.TableSize(t.Function));

        /// <summary>
        /// Lists the tables whose size differs from egglog's, or that eggnog lacks (size -1), as
        /// <c>name: egglog n, eggnog m</c>.
        /// </summary>
        /// <param name="expected">egglog's sizes.</param>
        /// <param name="actual">eggnog's sizes.</param>
        /// <returns>The differences, separated by semicolons; empty when the sizes agree.</returns>
        static string Diff(Dictionary<string, int> expected, Dictionary<string, int> actual) =>
            string.Join("; ", expected.Where(e => actual.TryGetValue(e.Key, out var a) == false || a != e.Value)
                .Select(e => $"{e.Key}: egglog {e.Value}, eggnog {(actual.TryGetValue(e.Key, out var a) ? a : -1)}"));

        /// <summary>
        /// Parses egglog's sizes, written <c>name size</c> and separated by commas.
        /// </summary>
        /// <param name="text">The sizes.</param>
        /// <returns>The sizes, by table name.</returns>
        static Dictionary<string, int> Parse(string text) =>
            text.Split(',').Select(p => p.Trim().Split(' ')).ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));

        [Fact]
        public void Literal01FirstBlockSizesMatchEgglog()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            c.DeclareFormUnder("kind", "B", "ref", "uuid-lower");
            var root = c.Graph.Root;
            var q = root.Make(c.And.Of(c.StrEq.Of("kind", "B"), c.UuidEq.Of("ref", Ref)));
            root.Assume(c.WhereClause.Of(q));
            root.Run(Schedule.Saturate(c.Horn, c.Rewrite));

            var expected = Parse("And 2, Assume 0, Not 0, Or 0, PFalse 0, PTrue 0, Query 1, Schema 1, StrEq 2, UuidEq 1, admits 4, conjunct 3, contradiction 0, discriminator 0, dom 2, form 1, inset 2, ne 0, parent 1, present 2, types 0, where 1");
            Assert.Equal(string.Empty, Diff(expected, Sizes(c.Graph)));
        }

        [Fact]
        public void Literal02SizesMatchEgglog()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var root = c.Graph.Root;
            root.Assume(
                c.Present.Of(c.Schema.Of(), "kind"),
                c.Dom.Of(c.Schema.Of(), "kind").Is(ValueSet.Of("order", "invoice", "note")),
                c.Discriminator.Of("kind", "order"),
                c.Discriminator.Of("kind", "invoice"),
                c.Discriminator.Of("kind", "note"));
            c.DeclareFormUnder("kind", "order", "ref", "uuid-lower");
            c.DeclareFormUnder("kind", "invoice", "ref", "uuid-lower");
            var q = root.Make(c.And.Of(c.Or.Of(c.StrEq.Of("kind", "order"), c.StrEq.Of("kind", "invoice")), c.UuidEq.Of("ref", Ref)));
            root.Assume(c.WhereClause.Of(q));
            root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));

            var expected = Parse("And 2, Assume 5, Not 0, Or 2, PFalse 0, PTrue 0, Query 1, Schema 1, StrEq 3, UuidEq 1, admits 6, conjunct 3, contradiction 0, discriminator 3, dom 10, dom-cases 3, dom-reported 3, form 5, form-cases 2, inset 4, ne 0, parent 6, present 10, present-cases 3, types 12, where 1");
            Assert.Equal(string.Empty, Diff(expected, Sizes(c.Graph)));
        }

        [Fact]
        public void Literal03SizesMatchEgglog()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            var root = c.Graph.Root;
            root.Assume(
                c.Present.Of(c.Schema.Of(), "kind"),
                c.Dom.Of(c.Schema.Of(), "kind").Is(ValueSet.Of("order", "invoice")));
            c.DeclareFormUnder("kind", "order", "ref", "uuid-lower");
            var q = root.Make(c.And.Of(c.Not.Of(c.StrEq.Of("kind", "invoice")), c.UuidEq.Of("ref", Ref)));
            root.Assume(c.WhereClause.Of(q));
            root.Run(Schedule.Saturate(c.Horn, c.Rewrite));
            root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));

            var expected = Parse("And 2, Assume 0, Not 1, Or 0, PFalse 0, PTrue 0, Query 1, Schema 1, StrEq 2, UuidEq 1, admits 3, conjunct 3, contradiction 0, discriminator 0, dom 3, form 1, inset 2, ne 1, parent 1, present 3, types 3, where 1");
            Assert.Equal(string.Empty, Diff(expected, Sizes(c.Graph)));
        }

    }

}
