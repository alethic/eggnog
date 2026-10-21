using System;
using System.Numerics;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// <see cref="BigRational"/>, egglog's <c>BigRat</c>: always in lowest terms with a positive denominator, as
    /// num-rational's <c>Ratio::new</c> reduces; ordered by value; rendered as num-rational displays it and as egglog's
    /// serializer prints it.
    /// </summary>
    public sealed class BigRationalTests
    {

        [Theory]
        [InlineData(2, 4, 1, 2)]
        [InlineData(-2, 4, -1, 2)]
        [InlineData(2, -4, -1, 2)]
        [InlineData(-2, -4, 1, 2)]
        [InlineData(0, -5, 0, 1)]
        [InlineData(6, 3, 2, 1)]
        public void AValueIsInLowestTermsWithAPositiveDenominator(
            long numerator, long denominator, long lowestNumerator, long lowestDenominator)
        {
            var value = new BigRational(numerator, denominator);

            Assert.Equal(lowestNumerator, value.Numerator);
            Assert.Equal(lowestDenominator, value.Denominator);
        }

        [Fact]
        public void DefaultIsZero()
        {
            var zero = default(BigRational);

            Assert.Equal(BigInteger.Zero, zero.Numerator);
            Assert.Equal(BigInteger.One, zero.Denominator);
            Assert.True(zero.IsInteger);
            Assert.Equal(new BigRational(0, 7), zero);
            Assert.Equal(new BigRational(0, 7).GetHashCode(), zero.GetHashCode());
            Assert.Equal("0", zero.ToString());
        }

        [Fact]
        public void AZeroDenominatorThrows() => Assert.Throws<DivideByZeroException>(() => new BigRational(1, 0));

        [Fact]
        public void EqualRationalsAreEqualAndHashAlike()
        {
            var a = new BigRational(3, 6);
            var b = new BigRational(-1, -2);

            Assert.Equal(a, b);
            Assert.True(a == b);
            Assert.False(a != b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(new BigRational(1, 2), new BigRational(1, 3));
        }

        [Fact]
        public void ValuesOrderByTheNumbersTheyAre()
        {
            var third = new BigRational(1, 3);
            var half = new BigRational(1, 2);
            var negative = new BigRational(-7, 2);

            Assert.True(third < half);
            Assert.True(negative < third);
            Assert.True(half >= third);
            Assert.True(half <= new BigRational(2, 4));
            Assert.Equal(0, half.CompareTo(new BigRational(2, 4)));
            Assert.True(((IComparable)half).CompareTo(null) > 0);
        }

        [Fact]
        public void TheArithmeticIsExact()
        {
            var third = new BigRational(1, 3);
            var sixth = new BigRational(1, 6);

            Assert.Equal(new BigRational(1, 2), third + sixth);
            Assert.Equal(sixth, third - sixth);
            Assert.Equal(new BigRational(1, 18), third * sixth);
            Assert.Equal(new BigRational(2), third / sixth);
            Assert.Equal(new BigRational(-1, 3), -third);
            Assert.Throws<DivideByZeroException>(() => third / default(BigRational));
        }

        [Fact]
        public void AValueRendersAsNumRationalDisplaysIt()
        {
            Assert.Equal("3/4", new BigRational(3, 4).ToString());
            Assert.Equal("-3/4", new BigRational(3, -4).ToString());
            Assert.Equal("5", new BigRational(10, 2).ToString());
            Assert.Equal("Ratio { numer: -3, denom: 4 }", new BigRational(3, -4).FormatDebug());
            Assert.Equal("Ratio { numer: 0, denom: 1 }", default(BigRational).FormatDebug());
        }

    }

}
