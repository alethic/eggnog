using System;
using System.Collections.Concurrent;
using System.Globalization;

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// A summary column that reports, for benchmark classes implementing <see cref="IItemsPerInvocation"/>, either the
    /// number of items one invocation processes or the throughput in items per second (items divided by the mean
    /// time of one invocation). This is how divan reports a bench with an <c>ItemsCount</c> counter, so the numbers
    /// line up with <c>cargo bench</c> output for the egglog counterparts.
    /// </summary>
    /// <remarks>
    /// The item count is computed once per benchmark case, apart from the measured run, by instantiating the benchmark
    /// class and setting the case's parameters on it (see <see cref="IItemsPerInvocation"/>), then cached.
    /// </remarks>
    internal sealed class ThroughputColumn : IColumn
    {

        /// <summary>
        /// The column showing the items processed per invocation.
        /// </summary>
        public static readonly IColumn Items = new ThroughputColumn(false);

        /// <summary>
        /// The column showing the items processed per second.
        /// </summary>
        public static readonly IColumn ItemsPerSecond = new ThroughputColumn(true);

        /// <summary>
        /// The item count of each benchmark case seen so far.
        /// </summary>
        static readonly ConcurrentDictionary<BenchmarkCase, long> counts = new();

        readonly bool rate;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rate"><c>true</c> for items per second; <c>false</c> for items per invocation.</param>
        ThroughputColumn(bool rate)
        {
            this.rate = rate;
        }

        /// <inheritdoc />
        public string Id => rate ? "Eggnog.ItemsPerSecond" : "Eggnog.Items";

        /// <inheritdoc />
        public string ColumnName => rate ? "Items/s" : "Items";

        /// <inheritdoc />
        public bool AlwaysShow => true;

        /// <inheritdoc />
        public ColumnCategory Category => ColumnCategory.Custom;

        /// <inheritdoc />
        public int PriorityInCategory => rate ? 1 : 0;

        /// <inheritdoc />
        public bool IsNumeric => true;

        /// <inheritdoc />
        public UnitType UnitType => UnitType.Dimensionless;

        /// <inheritdoc />
        public string Legend => rate
            ? "Items processed per second: Items divided by Mean (divan's throughput for an ItemsCount counter)"
            : "Items processed by one invocation (divan's ItemsCount input counter)";

        /// <inheritdoc />
        public bool IsAvailable(Summary summary) => true;

        /// <inheritdoc />
        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        /// <inheritdoc />
        public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, summary.Style);

        /// <inheritdoc />
        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        {
            var items = counts.GetOrAdd(benchmarkCase, CountItems);
            if (items <= 0)
                return "-";

            var culture = style.CultureInfo ?? CultureInfo.InvariantCulture;
            if (rate == false)
                return items.ToString("N0", culture);

            var mean = summary[benchmarkCase]?.ResultStatistics?.Mean;
            if (mean is not { } nanoseconds || nanoseconds <= 0)
                return "NA";

            return FormatRate(items / (nanoseconds / 1e9), culture);
        }

        /// <inheritdoc />
        public override string ToString() => ColumnName;

        /// <summary>
        /// Computes the items one invocation of <paramref name="benchmarkCase"/> processes, on a fresh instance of its
        /// class with the case's parameters set.
        /// </summary>
        /// <param name="benchmarkCase">The benchmark case.</param>
        /// <returns>The item count, or zero if the class does not count items.</returns>
        static long CountItems(BenchmarkCase benchmarkCase)
        {
            var type = benchmarkCase.Descriptor.Type;
            if (typeof(IItemsPerInvocation).IsAssignableFrom(type) == false)
                return 0;

            var instance = Activator.CreateInstance(type)!;
            foreach (var parameter in benchmarkCase.Parameters.Items)
            {
                var property = type.GetProperty(parameter.Name);
                if (property != null)
                    property.SetValue(instance, parameter.Value);
                else
                    type.GetField(parameter.Name)?.SetValue(instance, parameter.Value);
            }

            return ((IItemsPerInvocation)instance).ItemsPerInvocation(benchmarkCase.Descriptor.WorkloadMethod.Name);
        }

        /// <summary>
        /// Formats a rate with an SI prefix, as divan does (<c>12.34 Mitem/s</c>).
        /// </summary>
        /// <param name="perSecond">The items per second.</param>
        /// <param name="culture">The culture to format the number in.</param>
        /// <returns>The formatted rate.</returns>
        static string FormatRate(double perSecond, CultureInfo culture)
        {
            if (perSecond >= 1e9)
                return (perSecond / 1e9).ToString("F3", culture) + " Gitem/s";

            if (perSecond >= 1e6)
                return (perSecond / 1e6).ToString("F3", culture) + " Mitem/s";

            if (perSecond >= 1e3)
                return (perSecond / 1e3).ToString("F3", culture) + " Kitem/s";

            return perSecond.ToString("F3", culture) + " item/s";
        }

    }

}
