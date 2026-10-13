using System;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests for the extraction port: the on-demand extractor's choices against the root-sort extractor's,
    /// determinism of D10's content order, allocation on the per-row cost paths, and literal text. The review's ports of
    /// egglog's src/lib.rs extraction tests are in <see cref="ExtractorTests"/>.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class ExtractorReviewTests
    {

        [Fact]
        public void OnDemandChoiceDoesNotDependOnQueryOrder()
        {
            // Every head costs 0, so X = { G(Y), G(Z) } ties with Y = { M } and Z = { Zf }. Declaration order is
            // G < M < Zf. Run from scratch, Knuth's algorithm pops M (Y final, G(Y) queued), then G(Y) before Zf, so X
            // is G(Y). An on-demand extractor that costed Z first queues G(Z) as soon as X is discovered, and G(Z) pops
            // before M, so X becomes G(Z): the choice depends on which values were asked about earlier.
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
            // The same shape with positive head costs: M and Zf cost long.MaxValue, so every cost saturates and G's head
            // cost of 1 no longer separates a node from its child. The warmed on-demand extractor again picks G(Z).
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
            // D10 orders base values by content. BaseSort compares strings with Comparer<string>.Default, which is the
            // current culture's collation: under Danish collation "aa" sorts after "z", so the extracted term changes with
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
            // The existing test StringsTieByOrdinalContent uses "a" and "b", which every collation orders alike. Ordinal
            // order puts "B" (0x42) before "a" (0x61); culture collation puts "a" first.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var name = fx.Constructor("Name", e, 1, fx.String);
            var a = fx.Make(name, fx.Str("a"));
            var b = fx.Make(name, fx.Str("B"));
            fx.Union(a, b);
            Assert.Equal("(Name \"B\")", fx.Extract(e, a)?.Text);
        }

        [Fact]
        public void NodeCostFunctionsDoNotAllocatePerRow()
        {
            // AddNode builds the error message ("a row of " + name) before calling CostMath.Check, so every row costed by
            // a NodeCostFunction allocates a string (Extractor.Graph.cs:207).
            var (fx, e, num, rows) = ManyRows();
            var constant = MeasureConstruction(fx, e, CostModel.Default.WithFunctionCost(num, 1));
            var perRow = MeasureConstruction(fx, e, CostModel.Default.WithNodeCost(num, (_, _) => 1));
            Assert.True(perRow - constant < rows, $"a node cost function allocated {perRow - constant} extra bytes over {rows} rows");
        }

        [Fact]
        public void BaseValueCostFunctionsDoNotAllocatePerValue()
        {
            // BaseCost builds the error message ("a value of " + name) before calling CostMath.Check, so every base value
            // costed by a BaseValueCostFunction allocates a string (Extractor.cs:452).
            var (fx, e, num, rows) = ManyRows();
            var constant = MeasureConstruction(fx, e, CostModel.Default.WithFunctionCost(num, 1));
            var perValue = MeasureConstruction(fx, e, CostModel.Default.WithFunctionCost(num, 1).WithBaseValueCost(fx.I64, (_, _) => 1));
            Assert.True(perValue - constant < rows, $"a base value cost function allocated {perValue - constant} extra bytes over {rows} rows");
        }

        [Fact]
        public void StringLiteralsRenderAsEgglogDisplaysThem()
        {
            // egglog's base sorts reconstruct Literal::String / Literal::Float (src/sort/string.rs:38-47,
            // src/sort/f64.rs:54-63), and TermDag::to_string writes them with Literal's Display
            // (egglog-ast/src/generic_ast_helpers.rs:740-758): a string raw between quotes, a float with f64's Display
            // (never exponential) plus ".0" when it reads as an integer. BaseSort uses the codecs' Rust Debug format
            // instead, which escapes strings and writes exponents.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var name = fx.Constructor("Name", e, 1, fx.String);
            var quoted = fx.Make(name, fx.Str("say \"hi\""));

            Assert.Equal("(Name \"say \"hi\"\")", Extractor.ExtractValueToString(fx.Schema, e, quoted, out _));
        }

        [Fact]
        public void FloatLiteralsRenderAsEgglogDisplaysThem()
        {
            // The float half of StringLiteralsRenderAsEgglogDisplaysThem: egglog prints 1e20 as 100000000000000000000 and 1e-7
            // as 0.0000001 (f64 Display), where Rust Debug gives 1e20 and 1e-7.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var real = fx.Constructor("Real", e, 1, fx.F64);
            var big = fx.Make(real, fx.Float(1e20));
            var small = fx.Make(real, fx.Float(1e-7));

            Assert.Equal("(Real 100000000000000000000)", Extractor.ExtractValueToString(fx.Schema, e, big, out _));
            Assert.Equal("(Real 0.0000001)", Extractor.ExtractValueToString(fx.Schema, e, small, out _));
        }

        /// <summary>
        /// Builds an e-graph with 20000 rows of Num(i64).
        /// </summary>
        static (ExtractionFixture Fx, EqSort E, FunctionId Num, int Rows) ManyRows()
        {
            const int Rows = 20000;
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var num = fx.Constructor("Num", e, 1, fx.I64);
            fx.EGraph.AddValues(Enumerable.Range(0, Rows).Select(i => (num, new[] { fx.Int(i), fx.EGraph.FreshId() })).ToArray());
            return (fx, e, num, Rows);
        }

        /// <summary>
        /// Measures the bytes the current thread allocates to build a root-sort extractor, after a warm-up build.
        /// </summary>
        static long MeasureConstruction(ExtractionFixture fx, EqSort e, CostModel costs)
        {
            Extractor.ComputeCostsFromRootSorts(fx.Schema, [e], costs);
            var before = GC.GetAllocatedBytesForCurrentThread();
            Extractor.ComputeCostsFromRootSorts(fx.Schema, [e], costs);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

    }

}
