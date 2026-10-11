using System;
using System.Globalization;
using System.Numerics;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Pins <see cref="RustFloatDebug"/> to Rust's <c>Debug</c> for <c>f64</c> and <c>f32</c>. Every expected string was
    /// printed by rustc 1.99 (<c>format!("{:?}", x)</c>). The property tests check the output against .NET's shortest
    /// round-trip format: the same number of digits, reading back as the same float, and differing only where the value
    /// lies exactly halfway between two candidates, where Rust rounds up and .NET to even.
    /// </summary>
    public class RustFloatDebugTests
    {

        [Theory]
        [InlineData(0x4a5dc1e1u, "3633272.3")]
        [InlineData(0xc5dc7c40u, "-7055.5313")]
        [InlineData(0xc890cc84u, "-296548.13")]
        [InlineData(0x3f800000u, "1.0")]
        [InlineData(0x3dcccccdu, "0.1")]
        [InlineData(0x4b800000u, "16777216.0")]
        [InlineData(0x5a0e1bcau, "1e16")]
        [InlineData(0x5a0e1bc9u, "9999999000000000.0")]
        [InlineData(0x38d1b717u, "0.0001")]
        [InlineData(0x3727c5acu, "1e-5")]
        [InlineData(0x7f7fffffu, "3.4028235e38")]
        [InlineData(0x00800000u, "1.1754944e-38")]
        [InlineData(0x00000001u, "1e-45")]
        [InlineData(0x80000000u, "-0.0")]
        [InlineData(0x7fc00000u, "NaN")]
        [InlineData(0xff800000u, "-inf")]
        public void SinglesFormatLikeRustDebug(uint bits, string expected)
        {
            Assert.Equal(expected, RustFloatDebug.Format(BitConverter.UInt32BitsToSingle(bits)));
        }

        /// <summary>
        /// Subnormals, the smallest normal and powers of two (whose lower neighbor is nearer) and the edge of the
        /// exponential threshold.
        /// </summary>
        [Theory]
        [InlineData(0x0000000000000001UL, "5e-324")]
        [InlineData(0x0000000000000003UL, "1.5e-323")]
        [InlineData(0x000fffffffffffffUL, "2.225073858507201e-308")]
        [InlineData(0x0010000000000000UL, "2.2250738585072014e-308")]
        [InlineData(0x3ff0000000000000UL, "1.0")]
        [InlineData(0x4340000000000000UL, "9007199254740992.0")]
        [InlineData(0x7fefffffffffffffUL, "1.7976931348623157e308")]
        [InlineData(0x3f1a36e2eb1c432cUL, "9.999999999999999e-5")]
        [InlineData(0x3f1a36e2eb1c432dUL, "0.0001")]
        public void DoubleEdgesFormatLikeRustDebug(ulong bits, string expected)
        {
            Assert.Equal(expected, RustFloatDebug.Format(BitConverter.UInt64BitsToDouble(bits)));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void RandomDoublesMatchTheShortestRoundTripDigits(int seed)
        {
            var rng = new Random(seed);
            var bytes = new byte[8];
            for (var i = 0; i < 20000; i++)
            {
                rng.NextBytes(bytes);
                var value = BitConverter.ToDouble(bytes);

                // Every fourth value is a large integer plus a binary fraction, where ties are common.
                if (i % 4 == 0)
                    value = (double)rng.NextInt64(1L << 47, 1L << 54) + rng.Next(16) / 16.0;

                if (double.IsFinite(value) == false || value == 0)
                    continue;

                var text = RustFloatDebug.Format(value);
                Assert.Equal(value, double.Parse(text, CultureInfo.InvariantCulture));
                CheckAgainstShortest(text, value.ToString("R", CultureInfo.InvariantCulture), Exact(BitConverter.DoubleToUInt64Bits(value), 52, 11));
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void RandomSinglesMatchTheShortestRoundTripDigits(int seed)
        {
            var rng = new Random(seed);
            for (var i = 0; i < 20000; i++)
            {
                var value = BitConverter.UInt32BitsToSingle((uint)rng.NextInt64(0, 1L << 32));
                if (i % 4 == 0)
                    value = rng.Next(1 << 20, 1 << 26) + rng.Next(16) / 16.0f;

                if (float.IsFinite(value) == false || value == 0)
                    continue;

                var text = RustFloatDebug.Format(value);
                Assert.Equal(value, float.Parse(text, CultureInfo.InvariantCulture));
                CheckAgainstShortest(text, value.ToString("R", CultureInfo.InvariantCulture), Exact(BitConverter.SingleToUInt32Bits(value), 23, 8));
            }
        }

        /// <summary>
        /// Rust's digits must equal .NET's shortest digits, or exceed them by one unit in the last place exactly when
        /// the value is the midpoint of .NET's digits and that next candidate.
        /// </summary>
        static void CheckAgainstShortest(string rust, string shortest, (BigInteger Numerator, BigInteger Denominator) exact)
        {
            var (rustDigits, rustExponent) = Parse(rust);
            var (netDigits, netExponent) = Parse(shortest);
            Assert.Equal(netDigits.ToString(CultureInfo.InvariantCulture).Length, rustDigits.ToString(CultureInfo.InvariantCulture).Length);
            if (rustDigits == netDigits && rustExponent == netExponent)
                return;

            // Align both to the last place of .NET's digits.
            Assert.True(rustExponent >= netExponent, rust + " vs " + shortest);
            Assert.Equal(netDigits + 1, rustDigits * BigInteger.Pow(10, rustExponent - netExponent));

            // The midpoint (2 * digits + 1) / 2 * 10^exponent equals the exact value.
            var midNumerator = (2 * netDigits + 1) * (netExponent >= 0 ? BigInteger.Pow(10, netExponent) : BigInteger.One);
            var midDenominator = 2 * (netExponent < 0 ? BigInteger.Pow(10, -netExponent) : BigInteger.One);
            Assert.Equal(exact.Numerator * midDenominator, midNumerator * exact.Denominator);
        }

        /// <summary>
        /// Parses a decimal or exponential numeral into its significant digits, without leading or trailing zeros, and
        /// the decimal exponent of the last one. The sign is dropped.
        /// </summary>
        static (BigInteger Digits, int Exponent) Parse(string text)
        {
            text = text.TrimStart('-');
            var e = text.IndexOfAny(['e', 'E']);
            var exponent = e < 0 ? 0 : int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var mantissa = e < 0 ? text : text.Substring(0, e);
            var dot = mantissa.IndexOf('.');
            if (dot >= 0)
            {
                exponent -= mantissa.Length - dot - 1;
                mantissa = mantissa.Remove(dot, 1);
            }

            var trimmed = mantissa.TrimEnd('0');
            exponent += mantissa.Length - trimmed.Length;
            return (BigInteger.Parse(trimmed, CultureInfo.InvariantCulture), exponent);
        }

        /// <summary>
        /// The exact absolute value of a finite, nonzero IEEE float with the given field widths, as a fraction.
        /// </summary>
        static (BigInteger Numerator, BigInteger Denominator) Exact(ulong bits, int fractionBits, int exponentBits)
        {
            var bias = (1 << (exponentBits - 1)) - 1;
            var biased = (int)((bits >> fractionBits) & ((1UL << exponentBits) - 1));
            var mantissa = bits & ((1UL << fractionBits) - 1);
            if (biased != 0)
                mantissa |= 1UL << fractionBits;

            var exponent = Math.Max(biased, 1) - bias - fractionBits;
            return exponent >= 0
                ? (new BigInteger(mantissa) << exponent, BigInteger.One)
                : (new BigInteger(mantissa), BigInteger.One << -exponent);
        }

    }

}
