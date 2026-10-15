using System;

using Eggnog.Benchmarks.Comparison;

using Xunit;

namespace Eggnog.Tests.Benchmarks
{

    /// <summary>
    /// <see cref="CsvParser"/>: records and fields as BenchmarkDotNet's CSV exporter writes them.
    /// </summary>
    public sealed class CsvParserTests
    {

        [Fact]
        public void SplitsRecordsAndFields()
        {
            var records = CsvParser.Parse("Method,Rows\r\nScan,20000\nProbe,200000");
            Assert.Equal(3, records.Count);
            Assert.Equal(["Method", "Rows"], records[0]);
            Assert.Equal(["Scan", "20000"], records[1]);
            Assert.Equal(["Probe", "200000"], records[2]);
        }

        [Fact]
        public void QuotedFieldsKeepCommasQuotesAndLineBreaks()
        {
            var records = CsvParser.Parse("a,\"20,000\",\"say \"\"hi\"\"\",\"two\r\nlines\"\r\n");
            var record = Assert.Single(records);
            Assert.Equal(["a", "20,000", "say \"hi\"", "two\r\nlines"], record);
        }

        [Fact]
        public void KeepsEmptyFieldsAndSkipsBlankLines()
        {
            var records = CsvParser.Parse("\r\na,,b\r\n\r\nc,\r\n\r\n");
            Assert.Equal(2, records.Count);
            Assert.Equal(["a", "", "b"], records[0]);
            Assert.Equal(["c", ""], records[1]);
        }

        [Fact]
        public void EmptyTextHasNoRecords()
        {
            Assert.Empty(CsvParser.Parse(""));
        }

        [Fact]
        public void AnUnclosedQuoteIsAnError()
        {
            Assert.Throws<FormatException>(() => CsvParser.Parse("a,\"b\nc"));
        }

        [Fact]
        public void TextAfterAClosingQuoteIsAnError()
        {
            Assert.Throws<FormatException>(() => CsvParser.Parse("a,\"b\"c,d"));
        }

        [Fact]
        public void ASemicolonSeparatesFieldsWhenGiven()
        {
            var records = CsvParser.Parse("Method;Allocated\r\nScan;1,024 B\r\nProbe;\"a;b\"\r\n", ';');
            Assert.Equal(3, records.Count);
            Assert.Equal(["Method", "Allocated"], records[0]);
            Assert.Equal(["Scan", "1,024 B"], records[1]);
            Assert.Equal(["Probe", "a;b"], records[2]);
            Assert.Throws<FormatException>(() => CsvParser.Parse("a;\"b\",c", ';'));
        }

        [Theory]
        [InlineData("Method,Job;x\r\nRun;1", ',')]
        [InlineData("Method;Job,x\r\nRun,1", ';')]
        [InlineData("Method\r\nRun;1", ',')]
        [InlineData("Method\nRun;1", ',')]
        [InlineData("", ',')]
        public void TheSeparatorIsTheFirstCommaOrSemicolonOfTheHeader(string text, char expected)
        {
            Assert.Equal(expected, CsvParser.DetectSeparator(text));
        }

    }

}
