using System;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Allocation on the extractor's per-row cost paths: a cost function given per node or per base value allocates
    /// nothing per row beyond what a constant function cost allocates.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class ExtractorAllocationTests
    {

        [Fact]
        public void NodeCostFunctionsDoNotAllocatePerRow()
        {
            // AddNode passes CostMath.Check the parts of its error message ("a row of" and the function's name) instead
            // of a message built for every row, so a row costed by a NodeCostFunction allocates no string.
            var (fx, e, num, rows) = ManyRows();
            var constant = MeasureConstruction(fx, e, CostModel.Default.WithFunctionCost(num, 1));
            var perRow = MeasureConstruction(fx, e, CostModel.Default.WithNodeCost(num, (_, _) => 1));
            Assert.True(perRow - constant < rows, $"a node cost function allocated {perRow - constant} extra bytes over {rows} rows");
        }

        [Fact]
        public void BaseValueCostFunctionsDoNotAllocatePerValue()
        {
            // BaseCost passes CostMath.Check the parts of its error message ("a value of" and the sort's name), so a
            // base value costed by a BaseValueCostFunction allocates no string.
            var (fx, e, num, rows) = ManyRows();
            var constant = MeasureConstruction(fx, e, CostModel.Default.WithFunctionCost(num, 1));
            var perValue = MeasureConstruction(fx, e, CostModel.Default.WithFunctionCost(num, 1).WithBaseValueCost(fx.I64, (_, _) => 1));
            Assert.True(perValue - constant < rows, $"a base value cost function allocated {perValue - constant} extra bytes over {rows} rows");
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
