using System;

using Xunit;

namespace Eggnog.Tests.Terms
{

    /// <summary>
    /// egglog's <c>f64</c> primitives in <see cref="DoublePrimitives"/>: their egglog names, which are partial, and their
    /// values (<c>src/sort/f64.rs</c> at egglog 3.0.0). Division, remainder, <c>log</c> and <c>sqrt</c> have no result
    /// where egglog's have none, and every result that would be NaN, which no column holds, has none either.
    /// </summary>
    public sealed class DoublePrimitiveTests
    {

        public static TheoryData<Primitive, string, bool> Names => new()
        {
            { DoublePrimitives.Add, "+", true },
            { DoublePrimitives.Subtract, "-", true },
            { DoublePrimitives.Multiply, "*", true },
            { DoublePrimitives.Divide, "/", true },
            { DoublePrimitives.Remainder, "%", true },
            { DoublePrimitives.Power, "^", true },
            { DoublePrimitives.Negate, "neg", false },
            { DoublePrimitives.Abs, "abs", false },
            { DoublePrimitives.Min, "min", false },
            { DoublePrimitives.Max, "max", false },
            { DoublePrimitives.Exp, "exp", false },
            { DoublePrimitives.Log, "log", true },
            { DoublePrimitives.Sqrt, "sqrt", true },
            { DoublePrimitives.Less, "<", false },
            { DoublePrimitives.LessOrEqual, "<=", false },
            { DoublePrimitives.Greater, ">", false },
            { DoublePrimitives.GreaterOrEqual, ">=", false },
            { DoublePrimitives.ToInt64, "to-i64", false },
            { DoublePrimitives.Format, "to-string", false },
        };

        [Theory]
        [MemberData(nameof(Names))]
        public void EachPrimitiveHasEgglogsNameAndPartiality(Primitive primitive, string name, bool partial)
        {
            Assert.Equal(name, primitive.Name);
            Assert.Equal(partial, primitive.IsPartial);
        }

        [Fact]
        public void TheArithmeticIsIeee()
        {
            Assert.Equal(0.30000000000000004, Some(DoublePrimitives.Add, 0.1, 0.2));
            Assert.Equal(-1.5, Some(DoublePrimitives.Subtract, 1.0, 2.5));
            Assert.Equal(7.5, Some(DoublePrimitives.Multiply, 2.5, 3.0));
            Assert.Equal(3.5, Some(DoublePrimitives.Divide, 7.0, 2.0));
            Assert.Equal(double.PositiveInfinity, Some(DoublePrimitives.Add, double.MaxValue, double.MaxValue));
            Assert.Equal(double.NegativeInfinity, Some(DoublePrimitives.Multiply, double.PositiveInfinity, -1.0));
        }

        [Theory]
        [InlineData(5.5, 2.0, 1.5)]
        [InlineData(-5.5, 2.0, -1.5)]
        [InlineData(5.5, -2.0, 1.5)]
        public void TheRemainderTakesTheDividendsSign(double a, double b, double remainder) => Assert.Equal(remainder, Some(DoublePrimitives.Remainder, a, b));

        [Theory]
        [InlineData(1.0, 0.0)]
        [InlineData(0.0, 0.0)]
        [InlineData(double.PositiveInfinity, 0.0)]
        public void DivisionAndRemainderHaveNoResultForAZeroDivisor(double a, double b)
        {
            Assert.False(DoublePrimitives.Divide.TryInvoke(a, b, out _));
            Assert.False(DoublePrimitives.Remainder.TryInvoke(a, b, out _));
            Assert.False(DoublePrimitives.Divide.TryInvoke(a, -b, out _));
            Assert.False(DoublePrimitives.Remainder.TryInvoke(a, -b, out _));
        }

        [Fact]
        public void AResultThatWouldBeNaNHasNoResult()
        {
            Assert.False(DoublePrimitives.Add.TryInvoke(double.PositiveInfinity, double.NegativeInfinity, out _));
            Assert.False(DoublePrimitives.Subtract.TryInvoke(double.PositiveInfinity, double.PositiveInfinity, out _));
            Assert.False(DoublePrimitives.Multiply.TryInvoke(0.0, double.PositiveInfinity, out _));
            Assert.False(DoublePrimitives.Divide.TryInvoke(double.PositiveInfinity, double.PositiveInfinity, out _));
            Assert.False(DoublePrimitives.Remainder.TryInvoke(double.PositiveInfinity, 2.0, out _));
            Assert.False(DoublePrimitives.Power.TryInvoke(-8.0, 1.0 / 3.0, out _));
        }

        [Fact]
        public void PowerIsRustsPowf()
        {
            Assert.Equal(1024.0, Some(DoublePrimitives.Power, 2.0, 10.0));
            Assert.Equal(1.0, Some(DoublePrimitives.Power, 0.0, 0.0));
            Assert.Equal(0.25, Some(DoublePrimitives.Power, 2.0, -2.0));
            Assert.Equal(-8.0, Some(DoublePrimitives.Power, -2.0, 3.0));
            Assert.Equal(1.0, Some(DoublePrimitives.Power, -1.0, double.PositiveInfinity));
        }

        [Fact]
        public void LogAndSqrtHaveNoResultOutsideTheirDomains()
        {
            Assert.Equal(1.0, Some(DoublePrimitives.Log, Math.E));
            Assert.Equal(double.PositiveInfinity, Some(DoublePrimitives.Log, double.PositiveInfinity));
            Assert.False(DoublePrimitives.Log.TryInvoke(0.0, out _));
            Assert.False(DoublePrimitives.Log.TryInvoke(-1.0, out _));
            Assert.Equal(2.0, Some(DoublePrimitives.Sqrt, 4.0));
            Assert.Equal(0.0, Some(DoublePrimitives.Sqrt, 0.0));
            Assert.False(DoublePrimitives.Sqrt.TryInvoke(-1.0, out _));
            Assert.Throws<InvalidOperationException>(() => DoublePrimitives.Sqrt.Function(-1.0));
        }

        [Fact]
        public void TheTotalPrimitivesAreRusts()
        {
            Assert.Equal(-2.5, DoublePrimitives.Negate.Function(2.5));
            Assert.Equal(2.5, DoublePrimitives.Abs.Function(-2.5));
            Assert.Equal(1.0, DoublePrimitives.Min.Function(1.0, 2.0));
            Assert.Equal(2.0, DoublePrimitives.Max.Function(1.0, 2.0));
            Assert.Equal(1.0, DoublePrimitives.Exp.Function(0.0));
            Assert.Equal(double.PositiveInfinity, DoublePrimitives.Exp.Function(1000.0));
            Assert.True(DoublePrimitives.Less.Function(1.0, 2.0));
            Assert.True(DoublePrimitives.LessOrEqual.Function(2.0, 2.0));
            Assert.False(DoublePrimitives.Greater.Function(2.0, 2.0));
            Assert.True(DoublePrimitives.GreaterOrEqual.Function(2.0, 2.0));
        }

        /// <summary>
        /// <c>to-i64</c> is Rust's <c>as i64</c>: truncation toward zero, saturating at the ends.
        /// </summary>
        [Theory]
        [InlineData(2.9, 2)]
        [InlineData(-2.9, -2)]
        [InlineData(1e300, long.MaxValue)]
        [InlineData(-1e300, long.MinValue)]
        [InlineData(double.PositiveInfinity, long.MaxValue)]
        [InlineData(double.NegativeInfinity, long.MinValue)]
        [InlineData(9223372036854775808.0, long.MaxValue)]
        [InlineData(-9223372036854775808.0, long.MinValue)]
        [InlineData(9223372036854774784.0, 9223372036854774784)]
        public void ToInt64TruncatesAndSaturates(double value, long result) => Assert.Equal(result, DoublePrimitives.ToInt64.Function(value));

        /// <summary>
        /// <c>to-string</c> is Rust's <c>Debug</c>, as egglog's.
        /// </summary>
        [Theory]
        [InlineData(1.0, "1.0")]
        [InlineData(0.1, "0.1")]
        [InlineData(-2.5, "-2.5")]
        [InlineData(1e16, "1e16")]
        [InlineData(1.5e-7, "1.5e-7")]
        [InlineData(0.0001, "0.0001")]
        [InlineData(double.PositiveInfinity, "inf")]
        [InlineData(double.NegativeInfinity, "-inf")]
        public void FormatIsRustsDebug(double value, string text) => Assert.Equal(text, DoublePrimitives.Format.Function(value));

        static double Some(Primitive<double, double, double> primitive, double a, double b)
        {
            Assert.True(primitive.TryInvoke(a, b, out var result), $"{primitive.Name} {a} {b} has no result");
            return result;
        }

        static double Some(Primitive<double, double> primitive, double a)
        {
            Assert.True(primitive.TryInvoke(a, out var result), $"{primitive.Name} {a} has no result");
            return result;
        }

    }

}
