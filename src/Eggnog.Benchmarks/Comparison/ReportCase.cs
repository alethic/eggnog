namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// One benchmark case of one report: a method of a class with one combination of parameter values, and the
    /// statistics the comparison reads.
    /// </summary>
    /// <param name="Key">The case's identity across builds: <c>Class.Method(Name=Value, ...)</c>, the class without its
    /// namespace and the parameters in name order (<see cref="BenchmarkReport.CaseKey"/>).</param>
    /// <param name="Class">The class name, without its namespace.</param>
    /// <param name="MedianNanoseconds">The <c>Median</c> column in nanoseconds; <see cref="double.NaN"/> when the report
    /// has no such column or the case has no value.</param>
    /// <param name="AllocatedBytes">The <c>Allocated</c> column in bytes; <see cref="double.NaN"/> when absent.</param>
    internal sealed record ReportCase(string Key, string Class, double MedianNanoseconds, double AllocatedBytes);

}
