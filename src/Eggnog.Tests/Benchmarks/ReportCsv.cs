using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Benchmarks
{

    /// <summary>
    /// Writes report text in the layout of BenchmarkDotNet 0.15's CSV exporter under <c>--allStats</c>: <c>Method</c>,
    /// the job's characteristics, the parameters, then the statistics and the memory column, with the job values of an
    /// in-process run.
    /// </summary>
    internal static class ReportCsv
    {

        /// <summary>
        /// The job columns, in the exporter's order, and the values a default in-process job writes in them.
        /// </summary>
        static readonly (string Name, string Value)[] jobColumns =
        [
            ("Job", "DefaultJob"), ("AnalyzeLaunchVariance", "False"), ("EvaluateOverhead", "Default"),
            ("MaxAbsoluteError", "Default"), ("MaxRelativeError", "Default"), ("MinInvokeCount", "Default"),
            ("MinIterationTime", "Default"), ("OutlierMode", "Default"), ("Affinity", "11111111"),
            ("EnvironmentVariables", "Empty"), ("Jit", "RyuJit"), ("LargeAddressAware", "Default"), ("Platform", "X64"),
            ("PowerPlanMode", "67b4a053-3646-4532-affd-0535c9ea82a7"), ("Runtime", ".NET 10.0"),
            ("AllowVeryLargeObjects", "False"), ("Concurrent", "True"), ("CpuGroups", "False"), ("Force", "True"),
            ("HeapAffinitizeMask", "Default"), ("HeapCount", "Default"), ("NoAffinitize", "False"), ("RetainVm", "False"),
            ("Server", "True"), ("Arguments", "Default"), ("BuildConfiguration", "Default"), ("Clock", "Default"),
            ("EngineFactory", "Default"), ("NuGetReferences", "Default"), ("Toolchain", "InProcessEmitToolchain"),
            ("IsMutator", "Default"), ("InvocationCount", "1"), ("IterationCount", "Default"), ("IterationTime", "Default"),
            ("LaunchCount", "Default"), ("MaxIterationCount", "Default"), ("MaxWarmupIterationCount", "Default"),
            ("MemoryRandomization", "Default"), ("MinIterationCount", "Default"), ("MinWarmupIterationCount", "Default"),
            ("RunStrategy", "Default"), ("UnrollFactor", "1"), ("WarmupCount", "Default"),
        ];

        /// <summary>
        /// The statistic columns before <c>Median</c>.
        /// </summary>
        static readonly string[] beforeMedian = ["Mean", "Error", "StdErr", "StdDev", "Min", "Q1"];

        /// <summary>
        /// The statistic columns after <c>Median</c>, before <c>Allocated</c>.
        /// </summary>
        static readonly string[] afterMedian = ["Q3", "Max", "Op/s"];

        /// <summary>
        /// Writes a report.
        /// </summary>
        /// <param name="parameters">The parameter columns' names.</param>
        /// <param name="rows">The cases: the method, the parameter values in the order of
        /// <paramref name="parameters"/>, the median cell and the allocated cell.</param>
        /// <returns>The CSV text, with <c>\r\n</c> line ends.</returns>
        public static string Text(string[] parameters, params (string Method, string[] Values, string Median, string Allocated)[] rows) =>
            Text(parameters, withMedian: true, rows);

        /// <summary>
        /// Writes a report, with or without its <c>Median</c> column.
        /// </summary>
        /// <param name="parameters">The parameter columns' names.</param>
        /// <param name="withMedian">Whether to write the <c>Median</c> column.</param>
        /// <param name="rows">The cases.</param>
        /// <returns>The CSV text.</returns>
        public static string Text(string[] parameters, bool withMedian, params (string Method, string[] Values, string Median, string Allocated)[] rows)
        {
            var header = new List<string> { "Method" };
            header.AddRange(jobColumns.Select(c => c.Name));
            header.AddRange(parameters);
            header.AddRange(beforeMedian);
            if (withMedian)
                header.Add("Median");

            header.AddRange(afterMedian);
            header.Add("Allocated");

            var text = new StringBuilder();
            text.Append(string.Join(",", header.Select(Quote))).Append("\r\n");
            foreach (var (method, values, median, allocated) in rows)
            {
                if (values.Length != parameters.Length)
                    throw new ArgumentException("Each row needs one value per parameter.", nameof(rows));

                var fields = new List<string> { method };
                fields.AddRange(jobColumns.Select(c => c.Value));
                fields.AddRange(values);
                fields.AddRange(beforeMedian.Select(_ => median));
                if (withMedian)
                    fields.Add(median);

                fields.AddRange(afterMedian.Select(_ => median));
                fields.Add(allocated);
                text.Append(string.Join(",", fields.Select(Quote))).Append("\r\n");
            }

            return text.ToString();
        }

        /// <summary>
        /// Quotes a field that holds a comma or a quote, as the exporter does.
        /// </summary>
        /// <param name="field">The field.</param>
        /// <returns>The field as written.</returns>
        static string Quote(string field) =>
            field.Contains(',', StringComparison.Ordinal) || field.Contains('"', StringComparison.Ordinal) ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;

    }

}
