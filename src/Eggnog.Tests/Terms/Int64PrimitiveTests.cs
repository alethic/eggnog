using System;

using Xunit;

namespace Eggnog.Tests.Terms
{

    /// <summary>
    /// egglog's <c>i64</c> primitives in <see cref="Int64Primitives"/>: their egglog names, which are partial, and their
    /// values, which follow Rust's checked arithmetic (<c>src/sort/i64.rs</c> at egglog 3.0.0) and have no result where
    /// it returns none.
    /// </summary>
    public sealed class Int64PrimitiveTests
    {

        public static TheoryData<Primitive, string, bool> Names => new()
        {
            { Int64Primitives.Add, "+", true },
            { Int64Primitives.Subtract, "-", true },
            { Int64Primitives.Multiply, "*", true },
            { Int64Primitives.Divide, "/", true },
            { Int64Primitives.Remainder, "%", true },
            { Int64Primitives.BitwiseAnd, "&", false },
            { Int64Primitives.BitwiseOr, "|", false },
            { Int64Primitives.BitwiseXor, "^", false },
            { Int64Primitives.ShiftLeft, "<<", true },
            { Int64Primitives.ShiftRight, ">>", true },
            { Int64Primitives.BitwiseNot, "not-i64", false },
            { Int64Primitives.Log2, "log2", true },
            { Int64Primitives.Abs, "abs", true },
            { Int64Primitives.Min, "min", false },
            { Int64Primitives.Max, "max", false },
            { Int64Primitives.Equal, "bool-=", false },
            { Int64Primitives.Less, "bool-<", false },
            { Int64Primitives.LessOrEqual, "bool-<=", false },
            { Int64Primitives.Greater, "bool->", false },
            { Int64Primitives.GreaterOrEqual, "bool->=", false },
            { Int64Primitives.ToDouble, "to-f64", false },
            { Int64Primitives.Format, "to-string", false },
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
        public void AddAddsWithinRange(long a, long b, long sum) => Assert.Equal(sum, Some(Int64Primitives.Add, a, b));

        [Theory]
        [InlineData(long.MaxValue, 1)]
        [InlineData(long.MinValue, -1)]
        public void AddHasNoResultOnOverflow(long a, long b) => Assert.False(Int64Primitives.Add.TryInvoke(a, b, out _));

        [Theory]
        [InlineData(long.MinValue, 1)]
        [InlineData(long.MaxValue, -1)]
        [InlineData(0, long.MinValue)]
        public void SubtractHasNoResultOnOverflow(long a, long b) => Assert.False(Int64Primitives.Subtract.TryInvoke(a, b, out _));

        [Fact]
        public void SubtractSubtractsWithinRange()
        {
            Assert.Equal(-1, Some(Int64Primitives.Subtract, 2, 3));
            Assert.Equal(long.MinValue, Some(Int64Primitives.Subtract, -1, long.MaxValue));
        }

        [Theory]
        [InlineData(6, 7, 42)]
        [InlineData(-6, 7, -42)]
        [InlineData(long.MinValue, 1, long.MinValue)]
        [InlineData(4611686018427387904, -2, long.MinValue)]
        [InlineData(3037000499, 3037000499, 9223372030926249001)]
        public void MultiplyMultipliesWithinRange(long a, long b, long product) => Assert.Equal(product, Some(Int64Primitives.Multiply, a, b));

        [Theory]
        [InlineData(long.MaxValue, 2)]
        [InlineData(long.MinValue, -1)]
        [InlineData(4611686018427387904, 2)]
        [InlineData(3037000500, 3037000500)]
        public void MultiplyHasNoResultOnOverflow(long a, long b) => Assert.False(Int64Primitives.Multiply.TryInvoke(a, b, out _));

        [Theory]
        [InlineData(7, 2, 3, 1)]
        [InlineData(-7, 2, -3, -1)]
        [InlineData(7, -2, -3, 1)]
        [InlineData(-7, -2, 3, -1)]
        [InlineData(long.MinValue, 1, long.MinValue, 0)]
        public void DivisionTruncatesTowardZeroAndTheRemainderTakesTheDividendsSign(long a, long b, long quotient, long remainder)
        {
            Assert.Equal(quotient, Some(Int64Primitives.Divide, a, b));
            Assert.Equal(remainder, Some(Int64Primitives.Remainder, a, b));
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(0, 0)]
        [InlineData(long.MinValue, -1)]
        public void DivisionAndRemainderHaveNoResultForAZeroDivisorOrOverflow(long a, long b)
        {
            Assert.False(Int64Primitives.Divide.TryInvoke(a, b, out _));
            Assert.False(Int64Primitives.Remainder.TryInvoke(a, b, out _));
        }

        [Fact]
        public void TheBitwiseOperatorsActOnTheTwosComplement()
        {
            Assert.Equal(0b1000, Some(Int64Primitives.BitwiseAnd, 0b1100, 0b1010));
            Assert.Equal(0b1110, Some(Int64Primitives.BitwiseOr, 0b1100, 0b1010));
            Assert.Equal(0b0110, Some(Int64Primitives.BitwiseXor, 0b1100, 0b1010));
            Assert.Equal(-6, Int64Primitives.BitwiseNot.Function(5));
            Assert.Equal(5, Some(Int64Primitives.BitwiseAnd, -1, 5));
        }

        [Theory]
        [InlineData(1, 0, 1)]
        [InlineData(1, 63, long.MinValue)]
        [InlineData(3, 63, long.MinValue)]
        [InlineData(-1, 1, -2)]
        public void ShiftLeftLosesTheBitsShiftedOut(long a, long count, long result) => Assert.Equal(result, Some(Int64Primitives.ShiftLeft, a, count));

        [Theory]
        [InlineData(-1, 63, -1)]
        [InlineData(-8, 1, -4)]
        [InlineData(long.MaxValue, 62, 1)]
        [InlineData(-7, 1, -4)]
        public void ShiftRightIsArithmetic(long a, long count, long result) => Assert.Equal(result, Some(Int64Primitives.ShiftRight, a, count));

        [Theory]
        [InlineData(64)]
        [InlineData(-1)]
        [InlineData(4294967296)]
        [InlineData(long.MinValue)]
        public void ShiftsHaveNoResultForACountOutsideZeroToSixtyThree(long count)
        {
            Assert.False(Int64Primitives.ShiftLeft.TryInvoke(1, count, out _));
            Assert.False(Int64Primitives.ShiftRight.TryInvoke(1, count, out _));
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        [InlineData(3, 1)]
        [InlineData(1024, 10)]
        [InlineData(long.MaxValue, 62)]
        public void Log2IsTheFloorOfTheLogarithm(long a, long result) => Assert.Equal(result, Some(Int64Primitives.Log2, a));

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(long.MinValue)]
        public void Log2HasNoResultBelowOneAsInEgglog3(long a) => Assert.False(Int64Primitives.Log2.TryInvoke(a, out _));

        [Fact]
        public void AbsHasNoResultForTheMinimum()
        {
            Assert.Equal(5, Some(Int64Primitives.Abs, -5));
            Assert.Equal(long.MaxValue, Some(Int64Primitives.Abs, -long.MaxValue));
            Assert.False(Int64Primitives.Abs.TryInvoke(long.MinValue, out _));
        }

        [Fact]
        public void TheComparisonsAndEqualityReturnBooleans()
        {
            Assert.True(Int64Primitives.Equal.Function(3, 3));
            Assert.False(Int64Primitives.Equal.Function(3, 4));
            Assert.True(Int64Primitives.Less.Function(3, 4));
            Assert.True(Int64Primitives.LessOrEqual.Function(4, 4));
            Assert.False(Int64Primitives.Greater.Function(4, 4));
            Assert.True(Int64Primitives.GreaterOrEqual.Function(4, 4));
            Assert.Equal(3, Int64Primitives.Min.Function(3, 4));
            Assert.Equal(4, Int64Primitives.Max.Function(3, 4));
        }

        /// <summary>
        /// <c>bool-!=</c> is egglog's generic disequality as a <c>bool</c>, here over any value type, made once per type.
        /// </summary>
        [Fact]
        public void NotEqualIsEgglogsGenericBoolNotEqual()
        {
            Assert.Equal("bool-!=", Primitives.NotEqual<long>().Name);
            Assert.False(Primitives.NotEqual<long>().IsPartial);
            Assert.Same(Primitives.NotEqual<string>(), Primitives.NotEqual<string>());
            Assert.True(Primitives.NotEqual<long>().Function(1, 2));
            Assert.False(Primitives.NotEqual<string>().Function("a", "a"));
            Assert.False(Primitives.NotEqual<double>().Function(0.0, -0.0));
            Assert.False(Primitives.NotEqual<Set<long>>().Function(Set.Of(1L, 2L), Set.Of(2L, 1L)));
        }

        [Fact]
        public void TheConversionsAreRusts()
        {
            Assert.Equal(-3.0, Int64Primitives.ToDouble.Function(-3));
            Assert.Equal(9223372036854775808.0, Int64Primitives.ToDouble.Function(long.MaxValue));
            Assert.Equal("-12", Int64Primitives.Format.Function(-12));
            Assert.Equal("9223372036854775807", Int64Primitives.Format.Function(long.MaxValue));
        }

        /// <summary>
        /// A partial primitive's <c>Function</c> throws where there is no result, what .NET's own operation throws: an
        /// <see cref="OverflowException"/> for an overflow, a <see cref="DivideByZeroException"/> for a zero divisor, and
        /// an <see cref="InvalidOperationException"/> for a shift count or a logarithm out of range.
        /// </summary>
        [Fact]
        public void APartialFunctionThrowsWhereThereIsNoResult()
        {
            Assert.Throws<OverflowException>(() => Int64Primitives.Add.Function(long.MaxValue, 1));
            Assert.Throws<OverflowException>(() => Int64Primitives.Subtract.Function(long.MinValue, 1));
            Assert.Throws<OverflowException>(() => Int64Primitives.Multiply.Function(long.MaxValue, 2));
            Assert.Throws<OverflowException>(() => Int64Primitives.Divide.Function(long.MinValue, -1));
            Assert.Throws<OverflowException>(() => Int64Primitives.Remainder.Function(long.MinValue, -1));
            Assert.Throws<OverflowException>(() => Int64Primitives.Abs.Function(long.MinValue));
            Assert.Throws<DivideByZeroException>(() => Int64Primitives.Divide.Function(1, 0));
            Assert.Throws<DivideByZeroException>(() => Int64Primitives.Remainder.Function(1, 0));
            Assert.Throws<InvalidOperationException>(() => Int64Primitives.ShiftLeft.Function(1, 64));
            Assert.Throws<InvalidOperationException>(() => Int64Primitives.Log2.Function(0));
            Assert.Equal(3, Int64Primitives.Divide.Function(7, 2));
            Assert.Equal(-1, Int64Primitives.Remainder.Function(-7, 2));
            Assert.Equal(42, Int64Primitives.Multiply.Function(6, 7));
            Assert.Equal(5, Int64Primitives.Abs.Function(-5));
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
