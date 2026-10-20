using System.Linq;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// NaN is a value of <c>double</c>, as it is of egglog's <c>f64</c> (<c>OrderedFloat</c>): one NaN, equal to itself
    /// and above every other value, -0.0 the same value as 0.0.
    /// </summary>
    public sealed class NaNValueTests
    {

        [Fact]
        public void NaNIsWrittenAndReadAsOneValue()
        {
            var graph = new EGraph();
            var f = graph.DefineFunction<long, double>("f");
            graph.Root.Assume(f.Of(1L).Is(double.NaN));

            Assert.True(double.IsNaN(graph.Root.Get(f.Of(1L))));
            Assert.True(graph.Root.Check(f.Of(1L).Is(-double.NaN)));
            graph.Root.Assume(f.Of(1L).Is(double.NaN));
            Assert.Equal(1, graph.Root.CountRows(f));
        }

        [Fact]
        public void NaNSortsAboveEveryValue()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<double>("r");
            graph.Root.Assume(r.Of(double.NaN), r.Of(double.PositiveInfinity), r.Of(-1.0), r.Of(-0.0), r.Of(0.0));

            var rows = graph.Root.Rows(r).Select(row => (double)row.Keys[0]).ToArray();

            Assert.Equal(4, rows.Length);
            Assert.Equal(new[] { -1.0, 0.0, double.PositiveInfinity }, rows[..3]);
            Assert.True(double.IsNaN(rows[3]));
        }

        /// <summary>
        /// egglog's <c>:merge (max old new)</c> over <c>f64</c> keeps NaN, its greatest value, and <c>min</c> drops it.
        /// </summary>
        [Fact]
        public void TheMaxLatticeKeepsNaNAndTheMinLatticeDropsIt()
        {
            var graph = new EGraph();
            var high = graph.DefineFunction<long, double>("high", ValueLattice.Max<double>());
            var low = graph.DefineFunction<long, double>("low", ValueLattice.Min<double>());
            graph.Root.Assume(high.Of(1L).Is(5.0), low.Of(1L).Is(5.0));
            graph.Root.Assume(high.Of(1L).Is(double.NaN), low.Of(1L).Is(double.NaN));

            Assert.True(double.IsNaN(graph.Root.Get(high.Of(1L))));
            Assert.Equal(5.0, graph.Root.Get(low.Of(1L)));
        }

    }

}
