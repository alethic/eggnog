using System;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The extractor's choices are deterministic: the on-demand extractor chooses what the root-sort extractor chooses,
    /// whatever it was asked before, and ties between base values follow where their rows are stored, never the current
    /// culture's collation.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class ExtractorDeterminismTests
    {

        [Fact]
        public void OnDemandChoiceDoesNotDependOnQueryOrder()
        {
            // Every head costs 0, so X = { G(Y), G(Z) } ties with Y = { M } and Z = { Zf }. egglog's
            // (datatype E (G E :cost 0) (M :cost 0) (Zf :cost 0)) with these terms and the union extracts (G (M)): the
            // second pass meets G(Y) first. An on-demand extractor that costed Z first still answers so.
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
            // The same shape with positive head costs: M and Zf cost ulong.MaxValue, so every cost saturates and G's
            // head cost of 1 does not separate a node from its child. egglog extracts (G (M)), and so does the warmed
            // on-demand extractor.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var g = fx.Constructor("G", e, 1, e);
            var m = fx.Constructor("M", e, ulong.MaxValue);
            var zf = fx.Constructor("Zf", e, ulong.MaxValue);
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
            // Base values tie by where their rows are stored, as in egglog, not by content: under the current culture's
            // collation (Danish) "aa" sorts after "z", and the extracted term must not change with the thread's
            // culture.
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

    }

}
