using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// Adds the columns a latency is read at to a benchmark class's summary: the median (<c>Median</c>, the 50th
    /// percentile) and the 95th percentile (<c>P95</c>) of the iteration means, beside the mean. BenchmarkDotNet
    /// otherwise shows the median only when it judges it worth showing, or with the quartiles under <c>--allStats</c>,
    /// and the 95th percentile only when, for some case, it lies more than three standard deviations above the mean;
    /// with the attribute, every summary shows both.
    /// </summary>
    /// <remarks>
    /// Each statistic is taken over the iterations, and an iteration's figure is the mean of the invocations it ran, so
    /// <c>P95</c> is the tail of the iteration means, not of single invocations. Under the default job BenchmarkDotNet
    /// first drops the iterations above the upper outlier fence of the iteration means (Q3 + 1.5 IQR), and takes every
    /// statistic, <c>Mean</c>, <c>Median</c>, <c>P95</c> and <c>Max</c> among them, over the rest; a cold-start job
    /// such as <c>--job dry</c> drops none. An isolated iteration slow enough to lie above the fence (a collection, an
    /// interruption) is dropped: it shows only in its own <c>WorkloadActual</c> line of the run's output (the
    /// <c>WorkloadResult</c> lines skip it) and in the hint on removed outliers that BenchmarkDotNet prints after the
    /// summary. <c>P95</c> is interpolated between two of the kept means, so a slow stretch of a run that stays within
    /// the fence enters it only when it covers at least one in twenty of the iterations kept, and a stretch over about
    /// a quarter of the iterations or more raises the fence and is kept; a single slow iteration within the fence shows
    /// in <c>Mean</c> and <c>Max</c>, but in <c>P95</c> only in part, and not at all once 21 or more iterations are
    /// kept.
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
