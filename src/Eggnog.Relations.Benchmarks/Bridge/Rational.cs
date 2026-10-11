using System;
using System.Globalization;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// A normalized rational number of 64-bit parts: the stand-in for Rust's <c>num_rational::Rational64</c>, the base
    /// value type of the <c>rat</c> function in egglog-bridge's <c>examples/math.rs</c> (<see cref="BridgeMathProgram"/>).
    /// Interned as a base value through <see cref="InternedCodec{T}"/>, as egglog interns a <c>Rational64</c>.
    /// </summary>
    /// <remarks>
    /// The denominator is positive and coprime with the numerator (what <c>Rational64::new</c> guarantees), so equal
    /// numbers are equal values and intern to the same <see cref="Value"/>. The program only builds the constants
    /// <c>-1</c>, <c>0</c>, <c>1</c>, <c>2</c>, <c>3</c> and <c>7</c>; no arithmetic is needed.
    /// </remarks>
    internal readonly record struct Rational
    {

        /// <summary>
        /// Initializes the rational <paramref name="numerator"/> / <paramref name="denominator"/>, normalized.
        /// </summary>
        /// <param name="numerator">The numerator.</param>
        /// <param name="denominator">The denominator; not zero.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="denominator"/> is zero.</exception>
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
        public override string ToString() => Numerator.ToString(CultureInfo.InvariantCulture) + "/" + Denominator.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Gets the greatest common divisor of two non-negative numbers; 1 when both are zero.
        /// </summary>
        static long Gcd(long a, long b)
        {
            while (b != 0)
                (a, b) = (b, a % b);

            return a == 0 ? 1 : a;
        }

    }

}
