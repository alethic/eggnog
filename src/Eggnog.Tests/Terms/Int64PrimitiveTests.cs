using System;

using Xunit;

namespace Eggnog.Tests.Terms
{

    /// <summary>
    /// egglog's <c>i64</c> primitives in <see cref="Primitives"/>: their egglog names, which are partial, and their
    /// values, which follow Rust's checked arithmetic (<c>src/sort/i64.rs</c> at egglog 3.0.0) and have no result where
    /// it returns none.
    /// </summary>
    public sealed class Int64PrimitiveTests
    {

        public static TheoryData<Primitive, string, bool> Names => new()
        {
            { Primitives.Add, "+", true },
            { Primitives.Subtract, "-", true },
            { Primitives.Multiply, "*", true },
            { Primitives.Divide, "/", true },
            { Primitives.Remainder, "%", true },
            { Primitives.BitwiseAnd, "&", false },
            { Primitives.BitwiseOr, "|", false },
            { Primitives.BitwiseXor, "^", false },
            { Primitives.ShiftLeft, "<<", true },
            { Primitives.ShiftRight, ">>", true },
            { Primitives.BitwiseNot, "not-i64", false },
            { Primitives.Log2, "log2", true },
            { Primitives.Abs, "abs", true },
            { Primitives.Min, "min", false },
            { Primitives.Max, "max", false },
            { Primitives.Equal, "bool-=", false },
            { Primitives.Less, "<", false },
            { Primitives.LessOrEqual, "<=", false },
            { Primitives.Greater, ">", false },
            { Primitives.GreaterOrEqual, ">=", false },
            { Primitives.ToDouble, "to-f64", false },
            { Primitives.Format, "to-string", false },
        };

        [Theory]
        [MemberData(nameof(Names))]
        public void EachPrimitiveHasEgglogsNameAndPartiality(Primitive primitive, string name, bool partial)
        {
            Assert.Equal(name, primitive.Name);
            Assert.Equal(partial, primitive.IsPartial);
        }

        [Theory]
        [InlineData(2, 3, 5)]
        [InlineData(long.MaxValue, -1, long.MaxValue - 1)]
        [InlineData(long.MinValue, long.MaxValue, -1)]
        public void AddAddsWithinRange(long a, long b, long sum) => Assert.Equal(sum, Some(Primitives.Add, a, b));

        [Theory]
        [InlineData(long.MaxValue, 1)]
        [InlineData(long.MinValue, -1)]
        public void AddHasNoResultOnOverflow(long a, long b) => Assert.False(Primitives.Add.TryInvoke(a, b, out _));

        [Theory]
        [InlineData(long.MinValue, 1)]
        [InlineData(long.MaxValue, -1)]
        [InlineData(0, long.MinValue)]
        public void SubtractHasNoResultOnOverflow(long a, long b) => Assert.False(Primitives.Subtract.TryInvoke(a, b, out _));

        [Fact]
        public void SubtractSubtractsWithinRange()
        {
            Assert.Equal(-1, Some(Primitives.Subtract, 2, 3));
            Assert.Equal(long.MinValue, Some(Primitives.Subtract, -1, long.MaxValue));
        }

        [Theory]
        [InlineData(6, 7, 42)]
        [InlineData(-6, 7, -42)]
        [InlineData(long.MinValue, 1, long.MinValue)]
        [InlineData(4611686018427387904, -2, long.MinValue)]
        [InlineData(3037000499, 3037000499, 9223372030926249001)]
        public void MultiplyMultipliesWithinRange(long a, long b, long product) => Assert.Equal(product, Some(Primitives.Multiply, a, b));

        [Theory]
        [InlineData(long.MaxValue, 2)]
        [InlineData(long.MinValue, -1)]
        [InlineData(4611686018427387904, 2)]
        [InlineData(3037000500, 3037000500)]
        public void MultiplyHasNoResultOnOverflow(long a, long b) => Assert.False(Primitives.Multiply.TryInvoke(a, b, out _));

        [Theory]
        [InlineData(7, 2, 3, 1)]
        [InlineData(-7, 2, -3, -1)]
        [InlineData(7, -2, -3, 1)]
        [InlineData(-7, -2, 3, -1)]
        [InlineData(long.MinValue, 1, long.MinValue, 0)]
        public void DivisionTruncatesTowardZeroAndTheRemainderTakesTheDividendsSign(long a, long b, long quotient, long remainder)
        {
            Assert.Equal(quotient, Some(Primitives.Divide, a, b));
            Assert.Equal(remainder, Some(Primitives.Remainder, a, b));
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(0, 0)]
        [InlineData(long.MinValue, -1)]
        public void DivisionAndRemainderHaveNoResultForAZeroDivisorOrOverflow(long a, long b)
        {
            Assert.False(Primitives.Divide.TryInvoke(a, b, out _));
            Assert.False(Primitives.Remainder.TryInvoke(a, b, out _));
        }

        [Fact]
        public void TheBitwiseOperatorsActOnTheTwosComplement()
        {
            Assert.Equal(0b1000, Some(Primitives.BitwiseAnd, 0b1100, 0b1010));
            Assert.Equal(0b1110, Some(Primitives.BitwiseOr, 0b1100, 0b1010));
            Assert.Equal(0b0110, Some(Primitives.BitwiseXor, 0b1100, 0b1010));
            Assert.Equal(-6, Primitives.BitwiseNot.Function(5));
            Assert.Equal(5, Some(Primitives.BitwiseAnd, -1, 5));
        }

        [Theory]
        [InlineData(1, 0, 1)]
        [InlineData(1, 63, long.MinValue)]
        [InlineData(3, 63, long.MinValue)]
        [InlineData(-1, 1, -2)]
        public void ShiftLeftLosesTheBitsShiftedOut(long a, long count, long result) => Assert.Equal(result, Some(Primitives.ShiftLeft, a, count));

        [Theory]
        [InlineData(-1, 63, -1)]
        [InlineData(-8, 1, -4)]
        [InlineData(long.MaxValue, 62, 1)]
        [InlineData(-7, 1, -4)]
        public void ShiftRightIsArithmetic(long a, long count, long result) => Assert.Equal(result, Some(Primitives.ShiftRight, a, count));

        [Theory]
        [InlineData(64)]
        [InlineData(-1)]
        [InlineData(4294967296)]
        [InlineData(long.MinValue)]
        public void ShiftsHaveNoResultForACountOutsideZeroToSixtyThree(long count)
        {
            Assert.False(Primitives.ShiftLeft.TryInvoke(1, count, out _));
            Assert.False(Primitives.ShiftRight.TryInvoke(1, count, out _));
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        [InlineData(3, 1)]
        [InlineData(1024, 10)]
        [InlineData(long.MaxValue, 62)]
        public void Log2IsTheFloorOfTheLogarithm(long a, long result) => Assert.Equal(result, Some(Primitives.Log2, a));

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(long.MinValue)]
        public void Log2HasNoResultBelowOneAsInEgglog3(long a) => Assert.False(Primitives.Log2.TryInvoke(a, out _));

        [Fact]
        public void AbsHasNoResultForTheMinimum()
        {
            Assert.Equal(5, Some(Primitives.Abs, -5));
            Assert.Equal(long.MaxValue, Some(Primitives.Abs, -long.MaxValue));
            Assert.False(Primitives.Abs.TryInvoke(long.MinValue, out _));
        }

        [Fact]
        public void TheComparisonsAndEqualityReturnBooleans()
        {
            Assert.True(Primitives.Equal.Function(3, 3));
            Assert.False(Primitives.Equal.Function(3, 4));
            Assert.True(Primitives.Less.Function(3, 4));
            Assert.True(Primitives.LessOrEqual.Function(4, 4));
            Assert.False(Primitives.Greater.Function(4, 4));
            Assert.True(Primitives.GreaterOrEqual.Function(4, 4));
            Assert.Equal(3, Primitives.Min.Function(3, 4));
            Assert.Equal(4, Primitives.Max.Function(3, 4));
        }

        [Fact]
        public void TheConversionsAreRusts()
        {
            Assert.Equal(-3.0, Primitives.ToDouble.Function(-3));
            Assert.Equal(9223372036854775808.0, Primitives.ToDouble.Function(long.MaxValue));
            Assert.Equal("-12", Primitives.Format.Function(-12));
            Assert.Equal("9223372036854775807", Primitives.Format.Function(long.MaxValue));
        }

        /// <summary>
        /// A partial primitive's <c>Function</c> throws where there is no result: <see cref="Primitives.Add"/> and
        /// <see cref="Primitives.Subtract"/> an <see cref="OverflowException"/>, as they always have, the others an
        /// <see cref="InvalidOperationException"/>.
        /// </summary>
        [Fact]
        public void APartialFunctionThrowsWhereThereIsNoResult()
        {
            Assert.Throws<OverflowException>(() => Primitives.Add.Function(long.MaxValue, 1));
            Assert.Throws<OverflowException>(() => Primitives.Subtract.Function(long.MinValue, 1));
            Assert.Throws<InvalidOperationException>(() => Primitives.Divide.Function(1, 0));
            Assert.Throws<InvalidOperationException>(() => Primitives.Log2.Function(0));
            Assert.Equal(3, Primitives.Divide.Function(7, 2));
        }

        static long Some(Primitive<long, long, long> primitive, long a, long b)
        {
            Assert.True(primitive.TryInvoke(a, b, out var result), $"{primitive.Name} {a} {b} has no result");
            return result;
        }

        static long Some(Primitive<long, long> primitive, long a)
        {
            Assert.True(primitive.TryInvoke(a, out var result), $"{primitive.Name} {a} has no result");
            return result;
        }

    }

}
