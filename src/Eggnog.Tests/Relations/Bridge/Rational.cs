using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A normalized rational number of 64-bit parts, the stand-in for Rust's <c>Rational64</c> in egglog-bridge's
    /// <c>math_test</c>: interned as a base value through <see cref="InternedCodec{T}"/>. The denominator is positive and
    /// coprime with the numerator, so equal numbers are equal values.
    /// </summary>
    readonly record struct Rational
    {

        /// <summary>
        /// Initializes the rational <paramref name="numerator"/> / <paramref name="denominator"/>, normalized.
        /// </summary>
        public Rational(long numerator, long denominator)
        {
            if (denominator == 0)
                throw new ArgumentOutOfRangeException(nameof(denominator));

            if (denominator < 0)
            {
                numerator = -numerator;
                denominator = -denominator;
            }

            var gcd = Gcd(Math.Abs(numerator), denominator);
            Numerator = numerator / gcd;
            Denominator = denominator / gcd;
        }

        /// <summary>
        /// Gets the numerator.
        /// </summary>
        public long Numerator { get; }

        /// <summary>
        /// Gets the denominator, positive.
        /// </summary>
        public long Denominator { get; }

        /// <inheritdoc />
        public override string ToString() => Numerator + "/" + Denominator;

        static long Gcd(long a, long b)
        {
            while (b != 0)
                (a, b) = (b, a % b);

            return a == 0 ? 1 : a;
        }

    }

}
