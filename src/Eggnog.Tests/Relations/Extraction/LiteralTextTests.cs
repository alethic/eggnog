using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="LiteralText"/>: extracted literals print as egglog's <c>Literal</c> <c>Display</c> prints
    /// them (egglog-ast/src/generic_ast_helpers.rs:740-758). The float expectations are Rust's <c>format!("{}", x)</c>,
    /// plus <c>.0</c> when that parses as an <c>i64</c>.
    /// </summary>
    public class LiteralTextTests
    {

        [Theory]
        [InlineData(1.0, "1.0")]
        [InlineData(1.5, "1.5")]
        [InlineData(3.14, "3.14")]
        [InlineData(100.0, "100.0")]
        [InlineData(-2.5, "-2.5")]
        [InlineData(0.0, "0.0")]
        [InlineData(-0.0, "-0.0")]
        [InlineData(0.0001, "0.0001")]
        [InlineData(1e-5, "0.00001")]
        [InlineData(1.5e-7, "0.00000015")]
        [InlineData(-2.5e-5, "-0.000025")]
        [InlineData(1e16, "10000000000000000.0")]
        [InlineData(1.2345e17, "123450000000000000.0")]
        [InlineData(9.2e18, "9200000000000000000.0")]
        [InlineData(9.223372036854776e18, "9223372036854776000")]
        [InlineData(1e19, "10000000000000000000")]
        [InlineData(1e20, "100000000000000000000")]
        [InlineData(-1e20, "-100000000000000000000")]
        [InlineData(double.NaN, "NaN")]
        [InlineData(double.PositiveInfinity, "inf")]
        [InlineData(double.NegativeInfinity, "-inf")]
        public void FloatsPrintAsEgglogDisplaysThem(double value, string expected)
        {
            Assert.Equal(expected, LiteralText.Float(value));
        }

        [Fact]
        public void ExtremeFloatsPrintInFull()
        {
            // Display never uses an exponent: the smallest subnormal has 323 zeros after the point, the largest finite
            // double 292 zeros after its 17 digits.
            Assert.Equal("0." + new string('0', 323) + "5", LiteralText.Float(double.Epsilon));
            Assert.Equal("17976931348623157" + new string('0', 292), LiteralText.Float(double.MaxValue));
        }

        [Fact]
        public void StringsPrintRawBetweenQuotes()
        {
            Assert.Equal("\"\"", LiteralText.String(""));
            Assert.Equal("\"a\"b\\c\nd\"", LiteralText.String("a\"b\\c\nd"));
        }

    }

}
