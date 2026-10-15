using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Benchmarks.Comparison;

using Xunit;

namespace Eggnog.Tests.Benchmarks
{

    /// <summary>
    /// <see cref="BenchmarkReport"/>: the cases of a report, keyed by class, method and parameters, with their median
    /// and allocated columns.
    /// </summary>
    public sealed class BenchmarkReportTests
    {

        static readonly IReadOnlyDictionary<string, string> NoAbsent = new Dictionary<string, string>();

        /// <summary>
        /// A dry-run report of <c>ConstrainedScanBenchmarks.EqConst</c> as the benchmark app at the root baseline
        /// exported it under <c>--allStats</c>, verbatim but for two rows.
        /// </summary>
        const string DryRun =
            "Method,Job,AnalyzeLaunchVariance,EvaluateOverhead,MaxAbsoluteError,MaxRelativeError,MinInvokeCount,MinIterationTime,OutlierMode,Affinity,EnvironmentVariables,Jit,LargeAddressAware,Platform,PowerPlanMode,Runtime,AllowVeryLargeObjects,Concurrent,CpuGroups,Force,HeapAffinitizeMask,HeapCount,NoAffinitize,RetainVm,Server,Arguments,BuildConfiguration,Clock,EngineFactory,NuGetReferences,Toolchain,IsMutator,InvocationCount,IterationCount,IterationTime,LaunchCount,MaxIterationCount,MaxWarmupIterationCount,MemoryRandomization,MinIterationCount,MinWarmupIterationCount,RunStrategy,UnrollFactor,WarmupCount,Rows,Tombstones,Mean,Error,StdErr,StdDev,Min,Q1,Median,Q3,Max,Op/s,Items,Items/s,Allocated\r\n"
            + "EqConst,Dry,False,Default,Default,Default,Default,Default,Default,11111111,Empty,RyuJit,Default,X64,67b4a053-3646-4532-affd-0535c9ea82a7,.NET 10.0,False,True,False,True,Default,Default,False,False,True,Default,Default,Default,Default,Default,InProcessEmitToolchain,Default,Default,1,Default,1,Default,Default,Default,Default,Default,ColdStart,1,1,20000,False,4.244 ms,NA,0.000 ms,0.000 ms,4.244 ms,4.244 ms,4.244 ms,4.244 ms,4.244 ms,235.63,\"20,000\",4.713 Mitem/s,0 B\r\n"
            + "EqConst,Dry,False,Default,Default,Default,Default,Default,Default,11111111,Empty,RyuJit,Default,X64,67b4a053-3646-4532-affd-0535c9ea82a7,.NET 10.0,False,True,False,True,Default,Default,False,False,True,Default,Default,Default,Default,Default,InProcessEmitToolchain,Default,Default,1,Default,1,Default,Default,Default,Default,Default,ColdStart,1,1,200000,True,6.603 ms,NA,0.000 ms,0.000 ms,6.603 ms,6.603 ms,6.603 ms,6.603 ms,6.603 ms,151.45,\"200,000\",30.289 Mitem/s,1680 B\r\n";

        [Fact]
        public void ReadsTheExportersLayout()
        {
            var report = BenchmarkReport.Parse("ConstrainedScanBenchmarks", DryRun, NoAbsent);
            Assert.True(report.HasMedian);
            Assert.Equal(["Rows", "Tombstones"], report.Parameters);
            Assert.Equal(2, report.Cases.Count);
            Assert.Equal("ConstrainedScanBenchmarks.EqConst(Rows=20000, Tombstones=False)", report.Cases[0].Key);
            Assert.Equal("ConstrainedScanBenchmarks.EqConst(Rows=200000, Tombstones=True)", report.Cases[1].Key);
            Assert.Equal(4_244_000.0, report.Cases[0].MedianNanoseconds, 6);
            Assert.Equal(6_603_000.0, report.Cases[1].MedianNanoseconds, 6);
            Assert.Equal(0.0, report.Cases[0].AllocatedBytes);
            Assert.Equal(1680.0, report.Cases[1].AllocatedBytes);
            Assert.All(report.Cases, c => Assert.Equal("ConstrainedScanBenchmarks", c.Class));
        }

        [Fact]
        public void TheMedianNotTheMeanIsRead()
        {
            // Every statistic of the written row is 1 ms; make the mean 9 ms.
            var lines = ReportCsv.Text(["Rows"], ("Scan", ["20000"], "1.000 ms", "0 B")).Split("\r\n");
            var header = lines[0].Split(',');
            var fields = lines[1].Split(',');
            fields[Array.IndexOf(header, "Mean")] = "9.000 ms";
            var edited = lines[0] + "\r\n" + string.Join(",", fields) + "\r\n";

            var report = BenchmarkReport.Parse("ScanBenchmarks", edited, NoAbsent);
            Assert.Equal(1_000_000.0, Assert.Single(report.Cases).MedianNanoseconds, 6);
        }

        [Fact]
        public void ParametersAreKeyedInNameOrderWhateverTheColumnOrder()
        {
            var a = BenchmarkReport.Parse("X", ReportCsv.Text(["Rows", "Mode"], ("Run", ["20000", "Serial"], "1 ms", "0 B")), NoAbsent);
            var b = BenchmarkReport.Parse("X", ReportCsv.Text(["Mode", "Rows"], ("Run", ["Serial", "20000"], "1 ms", "0 B")), NoAbsent);
            Assert.Equal("X.Run(Mode=Serial, Rows=20000)", Assert.Single(a.Cases).Key);
            Assert.Equal(Assert.Single(a.Cases).Key, Assert.Single(b.Cases).Key);
        }

        [Fact]
        public void ACaseWithoutParametersIsClassDotMethod()
        {
            var report = BenchmarkReport.Parse("BridgeMathBenchmarks", ReportCsv.Text([], ("Math", [], "6.6 s", "9.61 GB")), NoAbsent);
            Assert.Equal("BridgeMathBenchmarks.Math", Assert.Single(report.Cases).Key);
        }

        [Fact]
        public void AnAbsentParameterTakesItsGivenValue()
        {
            var absent = new Dictionary<string, string> { ["Shards"] = "0" };
            var old = BenchmarkReport.Parse("WritesBenchmarks", ReportCsv.Text(["Threads"], ("ParallelInsert", ["1"], "420 ms", "632 MB")), absent);
            Assert.Equal("WritesBenchmarks.ParallelInsert(Shards=0, Threads=1)", Assert.Single(old.Cases).Key);
            Assert.Equal(["Threads"], old.Parameters);

            // A report that has the column keeps its own values.
            var current = BenchmarkReport.Parse("WritesBenchmarks", ReportCsv.Text(["Threads", "Shards"],
                ("ParallelInsert", ["1", "0"], "420 ms", "632 MB"),
                ("ParallelInsert", ["1", "16"], "430 ms", "632 MB")), absent);
            Assert.Equal(["WritesBenchmarks.ParallelInsert(Shards=0, Threads=1)", "WritesBenchmarks.ParallelInsert(Shards=16, Threads=1)"],
                current.Cases.Select(c => c.Key));
        }

        [Fact]
        public void AReportWithoutAMedianColumnHasNoMedians()
        {
            var report = BenchmarkReport.Parse("X", ReportCsv.Text(["Rows"], withMedian: false, ("Run", ["1"], "1 ms", "0 B")), NoAbsent);
            Assert.False(report.HasMedian);
            Assert.True(double.IsNaN(Assert.Single(report.Cases).MedianNanoseconds));
        }

        [Fact]
        public void AFailedCaseHasNoMedian()
        {
            var report = BenchmarkReport.Parse("X", ReportCsv.Text(["Rows"], ("Run", ["1"], "NA", "-")), NoAbsent);
            Assert.True(double.IsNaN(Assert.Single(report.Cases).MedianNanoseconds));
            Assert.True(double.IsNaN(report.Cases[0].AllocatedBytes));
        }

        [Fact]
        public void TheSameCaseTwiceIsAnError()
        {
            var text = ReportCsv.Text(["Rows"], ("Run", ["1"], "1 ms", "0 B"), ("Run", ["1"], "2 ms", "0 B"));
            Assert.Throws<FormatException>(() => BenchmarkReport.Parse("X", text, NoAbsent));
        }

        [Fact]
        public void ARowOfAnotherWidthIsAnError()
        {
            var text = ReportCsv.Text(["Rows"], ("Run", ["1"], "1 ms", "0 B")) + "Run,1\r\n";
            Assert.Throws<FormatException>(() => BenchmarkReport.Parse("X", text, NoAbsent));

            var wider = ReportCsv.Text(["Rows"], ("Run", ["2"], "1 ms", "0 B")).Split("\r\n")[1] + ",extra\r\n";
            var error = Assert.Throws<FormatException>(() => BenchmarkReport.Parse("X", ReportCsv.Text(["Rows"], ("Run", ["1"], "1 ms", "0 B")) + wider, NoAbsent));
            Assert.Contains("fields", error.Message);
        }

        [Fact]
        public void AReportSeparatedBySemicolonsIsRead()
        {
            // The exporter writes the current culture's list separator, a semicolon under de-DE; numbers stay invariant.
            var text = ReportCsv.Text(["Rows", "Mode"], ("Run", ["20000", "Serial"], "4.244 ms", "1680 B")).Replace(',', ';');
            var report = BenchmarkReport.Parse("X", text.Replace("1680 B", "1,680 B", StringComparison.Ordinal), NoAbsent);
            var single = Assert.Single(report.Cases);
            Assert.Equal("X.Run(Mode=Serial, Rows=20000)", single.Key);
            Assert.Equal(4_244_000.0, single.MedianNanoseconds, 6);
            Assert.Equal(1680.0, single.AllocatedBytes);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Job,Mean\r\nDry,1 ms\r\n")]
        [InlineData("Mean,Method\r\n1 ms,Run\r\n")]
        public void AReportWithoutMethodThenMeanIsAnError(string text)
        {
            Assert.Throws<FormatException>(() => BenchmarkReport.Parse("X", text, NoAbsent));
        }

        [Theory]
        [InlineData("results/Eggnog.Relations.Benchmarks.WritesBenchmarks-report.csv", "WritesBenchmarks")]
        [InlineData("WritesBenchmarks-report.csv", "WritesBenchmarks")]
        [InlineData("X.WritesBenchmarks-REPORT.CSV", "WritesBenchmarks")]
        [InlineData("results/Eggnog.Relations.Benchmarks.WritesBenchmarks-report-github.md", null)]
        [InlineData("-report.csv", null)]
        public void TheClassComesFromTheFileName(string path, string? expected)
        {
            Assert.Equal(expected, BenchmarkReport.ClassOfFile(path));
        }

    }

}
