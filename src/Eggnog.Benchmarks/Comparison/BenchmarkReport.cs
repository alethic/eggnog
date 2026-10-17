using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// The cases of one class's <c>*-report.csv</c>, as BenchmarkDotNet's CSV exporter writes it: a header row, then
    /// one row per case with the columns <c>Method</c>, the job's characteristics (<c>Job</c> to
    /// <c>WarmupCount</c>), the class's parameters, and the statistics from <c>Mean</c> on. The fields are separated by
    /// commas or, as the exporter writes them under a culture whose list separator is one, semicolons.
    /// </summary>
    /// <remarks>
    /// A case is identified across builds by its class name without the namespace, its method and its parameter values
    /// (<see cref="CaseKey"/>), not by its job, so builds run with the same job options match whatever their
    /// toolchain or runtime columns say. A build whose class has no column for a parameter that a later build added is
    /// matched through the absent-parameter defaults given to <see cref="Parse"/>.
    /// </remarks>
    internal sealed class BenchmarkReport
    {

        /// <summary>
        /// The suffix of a report file's name; the rest of the name is the class's full name.
        /// </summary>
        public const string FileSuffix = "-report.csv";

        /// <summary>
        /// The columns BenchmarkDotNet writes for a job's characteristics, which are not parameters.
        /// </summary>
        static readonly HashSet<string> jobColumns = new(StringComparer.Ordinal)
        {
            "Job", "AnalyzeLaunchVariance", "EvaluateOverhead", "MaxAbsoluteError", "MaxRelativeError", "MinInvokeCount",
            "MinIterationTime", "OutlierMode", "Affinity", "EnvironmentVariables", "Jit", "LargeAddressAware", "Platform",
            "PowerPlanMode", "Runtime", "AllowVeryLargeObjects", "Concurrent", "CpuGroups", "Force", "HeapAffinitizeMask",
            "HeapCount", "NoAffinitize", "RetainVm", "Server", "Arguments", "BuildConfiguration", "Clock", "EngineFactory",
            "NuGetReferences", "Toolchain", "IsMutator", "InvocationCount", "IterationCount", "IterationTime",
            "LaunchCount", "MaxIterationCount", "MaxWarmupIterationCount", "MemoryRandomization", "MinIterationCount",
            "MinWarmupIterationCount", "RunStrategy", "UnrollFactor", "WarmupCount",
        };

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="className">The class name, without its namespace.</param>
        /// <param name="parameters">The names of the report's parameter columns.</param>
        /// <param name="hasMedian">Whether the report has a <c>Median</c> column.</param>
        /// <param name="cases">The cases, in the report's order.</param>
        BenchmarkReport(string className, IReadOnlyList<string> parameters, bool hasMedian, IReadOnlyList<ReportCase> cases)
        {
            Class = className;
            Parameters = parameters;
            HasMedian = hasMedian;
            Cases = cases;
        }

        /// <summary>
        /// Gets the class name, without its namespace.
        /// </summary>
        public string Class { get; }

        /// <summary>
        /// Gets the names of the report's parameter columns, in the report's order; the parameters added for absent
        /// columns are not among them.
        /// </summary>
        public IReadOnlyList<string> Parameters { get; }

        /// <summary>
        /// Gets whether the report has a <c>Median</c> column. BenchmarkDotNet writes one when it judges the median
        /// worth showing, and always under <c>--allStats</c>.
        /// </summary>
        public bool HasMedian { get; }

        /// <summary>
        /// Gets the cases, in the report's order.
        /// </summary>
        public IReadOnlyList<ReportCase> Cases { get; }

        /// <summary>
        /// Gets the class name, without its namespace, that a report file's name gives: the name is the class's full
        /// name followed by <see cref="FileSuffix"/>.
        /// </summary>
        /// <param name="path">The file's path.</param>
        /// <returns>The class name; <c>null</c> when the file's name does not end with <see cref="FileSuffix"/>.</returns>
        public static string? ClassOfFile(string path)
        {
            ArgumentNullException.ThrowIfNull(path);

            var name = Path.GetFileName(path);
            if (name.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase) == false || name.Length == FileSuffix.Length)
                return null;

            var fullName = name.Substring(0, name.Length - FileSuffix.Length);
            return fullName.Substring(fullName.LastIndexOf('.') + 1);
        }

        /// <summary>
        /// Gets the key that identifies a case across builds: <c>Class.Method(Name=Value, ...)</c>, the parameters in
        /// ordinal order of their names; just <c>Class.Method</c> when there are none.
        /// </summary>
        /// <param name="className">The class name, without its namespace.</param>
        /// <param name="method">The method name.</param>
        /// <param name="parameters">The parameter names and values.</param>
        /// <returns>The key.</returns>
        public static string CaseKey(string className, string method, IEnumerable<KeyValuePair<string, string>> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);

            var ordered = parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value).ToList();
            return ordered.Count == 0 ? className + "." + method : className + "." + method + "(" + string.Join(", ", ordered) + ")";
        }

        /// <summary>
        /// Parses a report's text. Each parameter of <paramref name="absentParameters"/> that the report has no column
        /// for is added to every case with the given value, so that a build which predates the parameter matches the
        /// cases of its default value.
        /// </summary>
        /// <param name="className">The class name, without its namespace.</param>
        /// <param name="text">The CSV text.</param>
        /// <param name="absentParameters">The value each named parameter takes in a report without its column.</param>
        /// <returns>The report.</returns>
        /// <exception cref="FormatException">The text has no header, the header has no <c>Method</c> or <c>Mean</c>
        /// column, a row has another number of fields than the header, two rows are the same case, or a statistic does
        /// not parse.</exception>
        public static BenchmarkReport Parse(string className, string text, IReadOnlyDictionary<string, string> absentParameters)
        {
            ArgumentNullException.ThrowIfNull(className);
            ArgumentNullException.ThrowIfNull(absentParameters);

            var records = CsvParser.Parse(text, CsvParser.DetectSeparator(text));
            if (records.Count == 0)
                throw new FormatException($"The report of {className} is empty.");

            var header = records[0];
            var method = Array.IndexOf(header, "Method");
            var mean = Array.IndexOf(header, "Mean");
            if (method < 0 || mean < 0 || mean < method)
                throw new FormatException($"The report of {className} has no Method column followed by a Mean column.");

            var median = Array.IndexOf(header, "Median");
            var allocated = Array.IndexOf(header, "Allocated");
            var parameters = new List<int>();
            for (var column = method + 1; column < mean; column++)
                if (jobColumns.Contains(header[column]) == false)
                    parameters.Add(column);

            var added = absentParameters.Where(p => Array.IndexOf(header, p.Key) < 0).ToList();
            var cases = new List<ReportCase>(records.Count - 1);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var row = 1; row < records.Count; row++)
            {
                var fields = records[row];
                if (fields.Length != header.Length)
                    throw new FormatException($"Row {row} of the report of {className} has {fields.Length} fields; the header has {header.Length}.");

                var values = parameters.Select(c => new KeyValuePair<string, string>(header[c], fields[c])).Concat(added);
                var key = CaseKey(className, fields[method], values);
                if (keys.Add(key) == false)
                    throw new FormatException($"The report of {className} lists the case {key} twice.");

                cases.Add(new ReportCase(
                    key,
                    className,
                    median < 0 ? double.NaN : ReportUnits.ParseNanoseconds(fields[median]),
                    allocated < 0 ? double.NaN : ReportUnits.ParseBytes(fields[allocated])));
            }

            return new BenchmarkReport(className, parameters.Select(c => header[c]).ToList(), median >= 0, cases);
        }

    }

}
