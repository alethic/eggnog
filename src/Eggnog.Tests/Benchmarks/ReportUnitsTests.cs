using System;

using Eggnog.Benchmarks.Comparison;

using Xunit;

namespace Eggnog.Tests.Benchmarks
{

    /// <summary>
    /// <see cref="ReportUnits"/>: the time and size cells of BenchmarkDotNet's reports.
    /// </summary>
    public sealed class ReportUnitsTests
    {

        [Theory]
        [InlineData("12 ns", 12.0)]
        [InlineData("908.1 μs", 908_100.0)]
        [InlineData("908.1 µs", 908_100.0)]
        [InlineData("908.1 us", 908_100.0)]
        [InlineData("4.244 ms", 4_244_000.0)]
        [InlineData("1,234.5 ms", 1_234_500_000.0)]
        [InlineData("1.5 s", 1_500_000_000.0)]
        [InlineData("2 m", 120_000_000_000.0)]
        [InlineData("1 h", 3_600_000_000_000.0)]
        [InlineData("250", 250.0)]
        [InlineData(" 3.000 ms ", 3_000_000.0)]
        public void ParsesTimesIntoNanoseconds(string cell, double expected)
        {
            Assert.Equal(expected, ReportUnits.ParseNanoseconds(cell), 6);
        }

        [Theory]
        [InlineData("")]
        [InlineData("NA")]
        [InlineData("-")]
        [InlineData("?")]
        public void MarkersOfNoValueAreNaN(string cell)
        {
            Assert.True(double.IsNaN(ReportUnits.ParseNanoseconds(cell)));
            Assert.True(double.IsNaN(ReportUnits.ParseBytes(cell)));
        }

        [Theory]
        [InlineData("3360 B", 3360.0)]
        [InlineData("1.5 KB", 1536.0)]
        [InlineData("2 MB", 2_097_152.0)]
        [InlineData("1 GB", 1_073_741_824.0)]
        [InlineData("1,024 B", 1024.0)]
        [InlineData("1 TB", 1_099_511_627_776.0)]
        [InlineData("512", 512.0)]
        public void ParsesSizesIntoBytes(string cell, double expected)
        {
            Assert.Equal(expected, ReportUnits.ParseBytes(cell), 6);
        }

        [Theory]
        [InlineData("4 parsecs")]
        [InlineData("ms")]
        public void UnknownUnitsAndMissingNumbersAreErrors(string cell)
        {
            Assert.Throws<FormatException>(() => ReportUnits.ParseNanoseconds(cell));
            Assert.Throws<FormatException>(() => ReportUnits.ParseBytes(cell));
        }

        [Theory]
        [InlineData(12.0, "12.000 ns")]
        [InlineData(1e3, "1.000 us")]
        [InlineData(1e6, "1.000 ms")]
        [InlineData(1e9, "1.000 s")]
        [InlineData(-2e6, "-2.000 ms")]
        [InlineData(908_100.0, "908.100 us")]
        [InlineData(4_244_000.0, "4.244 ms")]
        [InlineData(6_600_000_000.0, "6.600 s")]
        [InlineData(double.NaN, "-")]
        public void FormatsWithTheLargestUnitAtLeastOne(double nanoseconds, string expected)
        {
            Assert.Equal(expected, ReportUnits.FormatNanoseconds(nanoseconds));
        }

    }

}
