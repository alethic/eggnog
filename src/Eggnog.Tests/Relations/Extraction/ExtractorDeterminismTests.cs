using System;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The extractor's choices are deterministic: the on-demand extractor chooses what the root-sort extractor chooses,
    /// whatever it was asked before, and ties between base values follow their content in ordinal order, never the
    /// current culture's collation.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class ExtractorDeterminismTests
    {

        [Fact]
        public void OnDemandChoiceDoesNotDependOnQueryOrder()
        {
            // Every head costs 0, so X = { G(Y), G(Z) } ties with Y = { M } and Z = { Zf }. Declaration order is G < M
            // < Zf. Run from scratch, Knuth's algorithm pops M (Y final, G(Y) queued), then G(Y) before Zf, so X is
            // G(Y). An on-demand extractor that costed Z first would queue G(Z) as soon as X is discovered, and G(Z)
            // would pop before M, making X G(Z): the choice would depend on which values were asked about earlier. It
            // is G(Y) either way.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var g = fx.Constructor("G", e, 0, e);
            var m = fx.Constructor("M", e, 0);
            var zf = fx.Constructor("Zf", e, 0);
            var y = fx.Make(m);
            var z = fx.Make(zf);
            var gy = fx.Make(g, y);
            var gz = fx.Make(g, z);
            fx.Union(gy, gz);
            var x = fx.Canon(gy);

            var expected = fx.Extract(e, x)!.Value.Text;
            Assert.Equal("(G (M))", expected);

            var fresh = Extractor.CreateOnDemand(fx.Schema);
            Assert.Equal(expected, fresh.ExtractTerm(e, x).ToString());

            var warmed = Extractor.CreateOnDemand(fx.Schema);
            Assert.True(warmed.TryGetCost(e, z, out _));
            Assert.Equal(expected, warmed.ExtractTerm(e, x).ToString());
        }

        [Fact]
        public void OnDemandChoiceDoesNotDependOnQueryOrderWithSaturatedCosts()
        {
            // The same shape with positive head costs: M and Zf cost long.MaxValue, so every cost saturates and G's
            // head cost of 1 does not separate a node from its child. The warmed on-demand extractor still picks G(Y).
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var g = fx.Constructor("G", e, 1, e);
            var m = fx.Constructor("M", e, long.MaxValue);
            var zf = fx.Constructor("Zf", e, long.MaxValue);
            var y = fx.Make(m);
            var z = fx.Make(zf);
            var gy = fx.Make(g, y);
            var gz = fx.Make(g, z);
            fx.Union(gy, gz);
            var x = fx.Canon(gy);

            var expected = fx.Extract(e, x)!.Value.Text;
            Assert.Equal("(G (M))", expected);

            var warmed = Extractor.CreateOnDemand(fx.Schema);
            Assert.True(warmed.TryGetCost(e, z, out _));
            Assert.Equal(expected, warmed.ExtractTerm(e, x).ToString());
        }

        [Fact]
        public void StringTiesDoNotDependOnTheCurrentCulture()
        {
            // Base values tie by content. BaseSort compares strings ordinally: Comparer<string>.Default is the current
            // culture's collation, under which (Danish) "aa" sorts after "z", so the extracted term would change with
            // the thread's culture.
            static string Run(CultureInfo culture)
            {
                var saved = CultureInfo.CurrentCulture;
                CultureInfo.CurrentCulture = culture;
                try
                {
                    var fx = new ExtractionFixture();
                    var e = new EqSort("E");
                    var name = fx.Constructor("Name", e, 1, fx.String);
                    var aa = fx.Make(name, fx.Str("aa"));
                    var b = fx.Make(name, fx.Str("b"));
                    fx.Union(aa, b);
                    return fx.Extract(e, aa)!.Value.Text;
                }
                finally
                {
                    CultureInfo.CurrentCulture = saved;
                }
            }

            var invariant = Run(CultureInfo.InvariantCulture);
            Assert.Equal(invariant, Run(new CultureInfo("da-DK")));
        }

        [Fact]
        public void StringTiesAreOrdinal()
        {
            // ExtractorTieBreakTests.StringsTieByOrdinalContent uses "a" and "b", which every collation orders alike.
            // Ordinal order puts "B" (0x42) before "a" (0x61); culture collation puts "a" first.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var name = fx.Constructor("Name", e, 1, fx.String);
            var a = fx.Make(name, fx.Str("a"));
            var b = fx.Make(name, fx.Str("B"));
            fx.Union(a, b);
            Assert.Equal("(Name \"B\")", fx.Extract(e, a)?.Text);
        }

    }

}
