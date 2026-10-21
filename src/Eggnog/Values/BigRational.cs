using System;
using System.Globalization;
using System.Numerics;

namespace Eggnog
{

    /// <summary>
    /// A rational number of any size, egglog's <c>BigRat</c> (num-rational's <c>BigRational</c>): always in lowest terms
    /// with a positive denominator, so that equal rationals have equal numerators and denominators.
    /// <c>default(BigRational)</c> is 0. Ordered by value; renders as num-rational displays one, <c>3/4</c>, or <c>3</c>
    /// when the denominator is 1. <see cref="BigRationalPrimitives"/> has egglog's primitives over it.
    /// </summary>
    public readonly struct BigRational : IEquatable<BigRational>, IComparable<BigRational>, IComparable
    {

        readonly BigInteger numerator;
        readonly BigInteger denominator;

        /// <summary>
        /// Initializes the rational <paramref name="numerator"/> / <paramref name="denominator"/>, in lowest terms.
        /// </summary>
        /// <param name="numerator">The numerator.</param>
        /// <param name="denominator">The denominator, not 0.</param>
        /// <exception cref="DivideByZeroException"><paramref name="denominator"/> is 0.</exception>
        public BigRational(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.IsZero)
                throw new DivideByZeroException("A BigRational's denominator is 0.");

            if (numerator.IsZero)
            {
                this.numerator = BigInteger.Zero;
                this.denominator = BigInteger.One;
                return;
            }

            var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
            if (denominator.Sign < 0)
                divisor = -divisor;

            this.numerator = divisor.IsOne ? numerator : numerator / divisor;
            this.denominator = divisor.IsOne ? denominator : denominator / divisor;
        }

        /// <summary>
        /// Initializes the integer <paramref name="value"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        public BigRational(BigInteger value)
        {
            numerator = value;
            denominator = BigInteger.One;
        }

        /// <summary>
        /// Initializes a rational already in lowest terms with a positive denominator; <paramref name="reduced"/> only tells
        /// this constructor from the one that reduces.
        /// </summary>
        BigRational(BigInteger numerator, BigInteger denominator, bool reduced)
        {
            this.numerator = numerator;
            this.denominator = denominator;
        }

        /// <summary>
        /// Gets the numerator, which carries the sign.
        /// </summary>
        public BigInteger Numerator => numerator;

        /// <summary>
        /// Gets the denominator, which is positive.
        /// </summary>
        public BigInteger Denominator => denominator.IsZero ? BigInteger.One : denominator;

        /// <summary>
        /// Gets whether the denominator is 1.
        /// </summary>
        public bool IsInteger => denominator.IsZero || denominator.IsOne;

        /// <summary>
        /// Gets the sign: -1, 0 or 1.
        /// </summary>
        public int Sign => numerator.Sign;

        /// <summary>
        /// Makes a rational of a numerator and a positive denominator with no common factor, without reducing it.
        /// </summary>
        internal static BigRational FromReduced(BigInteger numerator, BigInteger denominator) => new(numerator, denominator, true);

        /// <summary>
        /// Gets whether <paramref name="other"/> is the same number.
        /// </summary>
        /// <param name="other">The other value.</param>
        /// <returns>Whether the values are equal.</returns>
        public bool Equals(BigRational other) => numerator == other.numerator && Denominator == other.Denominator;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is BigRational other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(numerator, Denominator);

        /// <summary>
        /// Compares two values by the numbers they are.
        /// </summary>
        /// <param name="other">The other value.</param>
        /// <returns>Negative, zero or positive.</returns>
        public int CompareTo(BigRational other)
        {
            var left = Denominator;
            var right = other.Denominator;
            return left == right ? numerator.CompareTo(other.numerator) : (numerator * right).CompareTo(other.numerator * left);
        }

        /// <inheritdoc />
        int IComparable.CompareTo(object? obj) => obj switch
        {
            null => 1,
            BigRational other => CompareTo(other),
            _ => throw new ArgumentException("The object is not a BigRational.", nameof(obj)),
        };

        /// <summary>
        /// Renders the value as num-rational displays one: <c>3/4</c>, or <c>3</c> when the denominator is 1.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => IsInteger
            ? numerator.ToString(CultureInfo.InvariantCulture)
            : numerator.ToString(CultureInfo.InvariantCulture) + "/" + Denominator.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Converts an integer.
        /// </summary>
        /// <param name="value">The value.</param>
        public static implicit operator BigRational(BigInteger value) => new(value);

        /// <summary>
        /// Converts an integer.
        /// </summary>
        /// <param name="value">The value.</param>
        public static implicit operator BigRational(long value) => new(value);

        /// <summary>
        /// Gets whether two values are equal.
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>Whether they are equal.</returns>
        public static bool operator ==(BigRational left, BigRational right) => left.Equals(right);

        /// <summary>
        /// Gets whether two values differ.
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>Whether they differ.</returns>
        public static bool operator !=(BigRational left, BigRational right) => left.Equals(right) == false;

        /// <summary>
        /// Gets whether <paramref name="left"/> is less than <paramref name="right"/>.
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>Whether it is less.</returns>
        public static bool operator <(BigRational left, BigRational right) => left.CompareTo(right) < 0;

        /// <summary>
        /// Gets whether <paramref name="left"/> is greater than <paramref name="right"/>.
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>Whether it is greater.</returns>
        public static bool operator >(BigRational left, BigRational right) => left.CompareTo(right) > 0;

        /// <summary>
        /// Gets whether <paramref name="left"/> is at most <paramref name="right"/>.
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>Whether it is at most the other.</returns>
        public static bool operator <=(BigRational left, BigRational right) => left.CompareTo(right) <= 0;

        /// <summary>
        /// Gets whether <paramref name="left"/> is at least <paramref name="right"/>.
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>Whether it is at least the other.</returns>
        public static bool operator >=(BigRational left, BigRational right) => left.CompareTo(right) >= 0;

        /// <summary>
        /// Negates a value.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>Its negation.</returns>
        public static BigRational operator -(BigRational value) => FromReduced(-value.numerator, value.Denominator);

        /// <summary>
        /// Adds two values.
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>The sum.</returns>
        public static BigRational operator +(BigRational left, BigRational right) =>
            new(left.numerator * right.Denominator + right.numerator * left.Denominator, left.Denominator * right.Denominator);

        /// <summary>
        /// Subtracts one value from another.
        /// </summary>
        /// <param name="left">The value subtracted from.</param>
        /// <param name="right">The value subtracted.</param>
        /// <returns>The difference.</returns>
        public static BigRational operator -(BigRational left, BigRational right) =>
            new(left.numerator * right.Denominator - right.numerator * left.Denominator, left.Denominator * right.Denominator);

        /// <summary>
        /// Multiplies two values.
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>The product.</returns>
        public static BigRational operator *(BigRational left, BigRational right) =>
            new(left.numerator * right.numerator, left.Denominator * right.Denominator);

        /// <summary>
        /// Divides one value by another.
        /// </summary>
        /// <param name="left">The dividend.</param>
        /// <param name="right">The divisor, not 0.</param>
        /// <returns>The quotient.</returns>
        /// <exception cref="DivideByZeroException"><paramref name="right"/> is 0.</exception>
        public static BigRational operator /(BigRational left, BigRational right) =>
            new(left.numerator * right.Denominator, left.Denominator * right.numerator);

        /// <summary>
        /// Formats the value as Rust's derived <c>Debug</c> of num-rational's <c>Ratio</c> prints it, which egglog's
        /// serializer prints for a <c>BigRat</c>: <c>Ratio { numer: 3, denom: 4 }</c>.
        /// </summary>
        internal string FormatDebug() =>
            "Ratio { numer: " + numerator.ToString(CultureInfo.InvariantCulture)
            + ", denom: " + Denominator.ToString(CultureInfo.InvariantCulture) + " }";

    }

}
