using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// Adds the columns a latency is read at to a benchmark class's summary: the median (<c>Median</c>, the 50th
    /// percentile) and the 95th percentile (<c>P95</c>) of the iteration means, beside the mean. BenchmarkDotNet
    /// otherwise shows the median only when it judges it worth showing, or with the quartiles under <c>--allStats</c>,
    /// and no tail percentile either way.
    /// </summary>
    /// <remarks>
    /// Each statistic is taken over the iterations, and an iteration's figure is the mean of the invocations it ran,
    /// so <c>P95</c> is the tail of the iteration means, not of single invocations: it shows a slow stretch of a run
    /// (a collection, an interruption), not the slowest single query.
    /// </remarks>
    internal sealed class LatencyColumnsAttribute : ColumnConfigBaseAttribute
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public LatencyColumnsAttribute() : base(StatisticColumn.Median, StatisticColumn.P95)
        {
        }

    }

}
