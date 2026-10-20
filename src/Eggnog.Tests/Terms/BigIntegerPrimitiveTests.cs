using System;
using System.Numerics;

using Xunit;

namespace Eggnog.Tests.Terms
{

    /// <summary>
    /// egglog's <c>BigInt</c> primitives in <see cref="BigIntegerPrimitives"/>: their egglog names, which are partial,
    /// and their values, which follow num-bigint as egglog 3.0.0 uses it (<c>src/sort/bigint.rs</c>).
    /// </summary>
    public sealed class BigIntegerPrimitiveTests
    {

        public static TheoryData<Primitive, string, bool> Names => new()
        {
            { BigIntegerPrimitives.FromInt64, "bigint", false },
            { BigIntegerPrimitives.Add, "+", false },
            { BigIntegerPrimitives.Subtract, "-", false },
            { BigIntegerPrimitives.Multiply, "*", false },
            { BigIntegerPrimitives.Divide, "/", true },
            { BigIntegerPrimitives.Remainder, "%", true },
            { BigIntegerPrimitives.BitwiseAnd, "&", false },
            { BigIntegerPrimitives.BitwiseOr, "|", false },
            { BigIntegerPrimitives.BitwiseXor, "^", false },
            { BigIntegerPrimitives.ShiftLeft, "<<", true },
            { BigIntegerPrimitives.ShiftRight, ">>", true },
            { BigIntegerPrimitives.BitwiseNot, "not-Z", false },
            { BigIntegerPrimitives.Bits, "bits", false },
            { BigIntegerPrimitives.Min, "min", false },
            { BigIntegerPrimitives.Max, "max", false },
            { BigIntegerPrimitives.Equal, "bool-=", false },
            { BigIntegerPrimitives.Less, "<", false },
            { BigIntegerPrimitives.LessOrEqual, "<=", false },
            { BigIntegerPrimitives.Greater, ">", false },
            { BigIntegerPrimitives.GreaterOrEqual, ">=", false },
            { BigIntegerPrimitives.Format, "to-string", false },
            { BigIntegerPrimitives.Parse, "from-string", true },
        };

        [Theory]
        [MemberData(nameof(Names))]
        public void EachPrimitiveHasEgglogsNameAndPartiality(Primitive primitive, string name, bool partial)
        {
            Assert.Equal(name, primitive.Name);
            Assert.Equal(partial, primitive.IsPartial);
        }

        [Fact]
        public void TheArithmeticDoesNotOverflow()
        {
            var big = BigIntegerPrimitives.FromInt64.Function(long.MaxValue);
            Assert.Equal(BigInteger.Parse("85070591730234615847396907784232501249"), BigIntegerPrimitives.Multiply.Function(big, big));
            Assert.Equal(BigInteger.Parse("18446744073709551614"), BigIntegerPrimitives.Add.Function(big, big));
            Assert.Equal(BigInteger.Parse("-18446744073709551615"), BigIntegerPrimitives.Subtract.Function(-big, big + 1));
        }

        [Theory]
        [InlineData(7, 2, 3, 1)]
        [InlineData(-7, 2, -3, -1)]
        [InlineData(7, -2, -3, 1)]
        [InlineData(-7, -2, 3, -1)]
        public void DivisionTruncatesTowardZeroAndTheRemainderTakesTheDividendsSign(long a, long b, long quotient, long remainder)
        {
            Assert.True(BigIntegerPrimitives.Divide.TryInvoke(a, b, out var q));
            Assert.True(BigIntegerPrimitives.Remainder.TryInvoke(a, b, out var r));
            Assert.Equal(quotient, q);
            Assert.Equal(remainder, r);
        }

        [Fact]
        public void DivisionAndRemainderHaveNoResultForAZeroDivisor()
        {
            Assert.False(BigIntegerPrimitives.Divide.TryInvoke(1, BigInteger.Zero, out _));
            Assert.False(BigIntegerPrimitives.Remainder.TryInvoke(1, BigInteger.Zero, out _));
            Assert.Throws<DivideByZeroException>(() => BigIntegerPrimitives.Divide.Function(1, BigInteger.Zero));
            Assert.Throws<InvalidOperationException>(() => BigIntegerPrimitives.Parse.Function("x"));
        }

        [Fact]
        public void TheBitwiseOperatorsActOnTheTwosComplement()
        {
            Assert.Equal(5, BigIntegerPrimitives.BitwiseAnd.Function(-1, 5));
            Assert.Equal(-1, BigIntegerPrimitives.BitwiseOr.Function(-2, 1));
            Assert.Equal(-2, BigIntegerPrimitives.BitwiseXor.Function(-1, 1));
            Assert.Equal(-6, BigIntegerPrimitives.BitwiseNot.Function(5));
            Assert.Equal(4, BigIntegerPrimitives.BitwiseNot.Function(-5));
        }

        [Fact]
        public void ShiftsAreArithmeticAndPartialOutsideTheU32Range()
        {
            Assert.Equal(BigInteger.Pow(2, 100), Some(BigIntegerPrimitives.ShiftLeft, 1, 100));
            Assert.Equal(-3, Some(BigIntegerPrimitives.ShiftRight, -5, 1));
            Assert.Equal(-1, Some(BigIntegerPrimitives.ShiftRight, -5, 100));
            Assert.Equal(0, Some(BigIntegerPrimitives.ShiftRight, 5, 100));
            Assert.False(BigIntegerPrimitives.ShiftLeft.TryInvoke(1, -1, out _));
            Assert.False(BigIntegerPrimitives.ShiftRight.TryInvoke(1, -1, out _));
            Assert.False(BigIntegerPrimitives.ShiftLeft.TryInvoke(1, 4294967296, out _));
            Assert.False(BigIntegerPrimitives.ShiftRight.TryInvoke(1, 4294967296, out _));
        }

        /// <summary>
        /// A count above <c>int.MaxValue</c>, which egglog takes as a <c>u32</c>, shifts every bit of a
        /// <see cref="BigInteger"/> out to the right. To the left, a result longer than <c>int.MaxValue</c> bits throws
        /// before it is computed, on every runtime.
        /// </summary>
        [Fact]
        public void ShiftsBeyondTheLargestBigInteger()
        {
            const long count = (long)int.MaxValue + 1;
            Assert.Equal(-1, Some(BigIntegerPrimitives.ShiftRight, -3, count));
            Assert.Equal(0, Some(BigIntegerPrimitives.ShiftRight, 3, count));
            Assert.Equal(0, Some(BigIntegerPrimitives.ShiftLeft, 0, uint.MaxValue));
            Assert.Throws<OverflowException>(() => BigIntegerPrimitives.ShiftLeft.TryInvoke(1, count, out _));
            Assert.Throws<OverflowException>(() => BigIntegerPrimitives.ShiftLeft.TryInvoke(1, int.MaxValue, out _));
            Assert.Throws<OverflowException>(() => BigIntegerPrimitives.ShiftLeft.TryInvoke(-1024, int.MaxValue - 10, out _));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(255, 8)]
        [InlineData(256, 9)]
        [InlineData(-1, 1)]
        [InlineData(-128, 8)]
        [InlineData(-129, 8)]
        public void BitsCountsTheMagnitudesBits(long value, long bits) => Assert.Equal(bits, BigIntegerPrimitives.Bits.Function(value));

        [Fact]
        public void TheComparisonsReturnBooleans()
        {
            BigInteger three = 3, four = 4;
            Assert.True(BigIntegerPrimitives.Equal.Function(three, 3));
            Assert.True(BigIntegerPrimitives.Less.Function(three, four));
            Assert.True(BigIntegerPrimitives.LessOrEqual.Function(four, four));
            Assert.False(BigIntegerPrimitives.Greater.Function(four, four));
            Assert.True(BigIntegerPrimitives.GreaterOrEqual.Function(four, four));
            Assert.Equal(three, BigIntegerPrimitives.Min.Function(three, four));
            Assert.Equal(four, BigIntegerPrimitives.Max.Function(three, four));
        }

        [Fact]
        public void FormatIsDecimal()
        {
            Assert.Equal("-123", BigIntegerPrimitives.Format.Function(-123));
            Assert.Equal("85070591730234615847396907784232501249", BigIntegerPrimitives.Format.Function(BigInteger.Pow(long.MaxValue, 2)));
        }

        /// <summary>
        /// <c>from-string</c> parses as num-bigint's <c>from_str_radix(s, 10)</c> does.
        /// </summary>
        [Theory]
        [InlineData("123", 123)]
        [InlineData("-123", -123)]
        [InlineData("+123", 123)]
        [InlineData("-0", 0)]
        [InlineData("1_000", 1000)]
        [InlineData("1__0_", 10)]
        [InlineData("007", 7)]
        public void ParseReadsNumBigintsDecimal(string text, long value)
        {
            Assert.True(BigIntegerPrimitives.Parse.TryInvoke(text, out var result));
            Assert.Equal(value, result);
        }

        [Theory]
        [InlineData("")]
        [InlineData("-")]
        [InlineData("+")]
        [InlineData("_1")]
        [InlineData("-_1")]
        [InlineData("+_1")]
        [InlineData("+-1")]
        [InlineData("-+1")]
        [InlineData("++1")]
        [InlineData("--1")]
        [InlineData(" 1")]
        [InlineData("1 ")]
        [InlineData("1.0")]
        [InlineData("1e3")]
        [InlineData("0x10")]
        [InlineData("١")]
        public void ParseHasNoResultForOtherText(string text) => Assert.False(BigIntegerPrimitives.Parse.TryInvoke(text, out _));

        static BigInteger Some(Primitive<BigInteger, long, BigInteger> primitive, BigInteger a, long b)
        {
            Assert.True(primitive.TryInvoke(a, b, out var result), $"{primitive.Name} {a} {b} has no result");
            return result;
        }

    }

}
