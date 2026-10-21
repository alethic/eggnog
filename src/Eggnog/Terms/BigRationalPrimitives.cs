using System;
using System.Numerics;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>BigRat</c> primitives over <see cref="BigRational"/>, with egglog's names (<c>src/sort/bigrat.rs</c>),
    /// computed as num-rational computes them.
    /// </summary>
    /// <remarks>
    /// Partial where egglog's are (<see cref="Primitive.IsPartial"/>): <see cref="Add"/>, <see cref="Subtract"/> and
    /// <see cref="Multiply"/>, whose checked arithmetic always has a result over big rationals; <see cref="Divide"/>, which
    /// has none for a zero divisor; <see cref="FromBigIntegers"/>, which has none for a zero denominator, as in egglog
    /// 3.0.0 (before it, egglog panics); <see cref="ToInt64"/>, which has none for a value that is not an
    /// integer or outside <c>i64</c>; <see cref="Power"/>, <see cref="Sqrt"/>, <see cref="Log"/> and <see cref="Cbrt"/>.
    /// No result drops the match in a rule body and fails the rule in a head; a partial primitive's <c>Function</c>
    /// throws there, <see cref="DivideByZeroException"/> for a zero divisor or denominator and
    /// <see cref="InvalidOperationException"/> otherwise. egglog's <c>log</c> and <c>cbrt</c> are unfinished
    /// (<c>todo!</c>) except at 1: here, as there, a call at any other value fails the rule, with a
    /// <see cref="NotSupportedException"/>. The comparisons return a <c>bool</c>: as a filter
    /// (<see cref="RuleBuilder.Where(Term{bool})"/>) they are egglog's <c>&lt;</c> family, and render so; as a value they
    /// render as <c>bool-&lt;</c> and the like, an addition, since egglog compares <c>BigRat</c> values only as filters.
    /// </remarks>
    public static class BigRationalPrimitives
    {

        /// <summary>
        /// Gets <c>+</c> over <c>BigRat</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational, BigRational> Add { get; } =
            BuiltInPrimitive.Partial<BigRational, BigRational, BigRational>("+", static (a, b) => a + b, TryAdd);

        /// <summary>
        /// Gets <c>-</c> over <c>BigRat</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational, BigRational> Subtract { get; } =
            BuiltInPrimitive.Partial<BigRational, BigRational, BigRational>("-", static (a, b) => a - b, TrySubtract);

        /// <summary>
        /// Gets <c>*</c> over <c>BigRat</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational, BigRational> Multiply { get; } =
            BuiltInPrimitive.Partial<BigRational, BigRational, BigRational>("*", static (a, b) => a * b, TryMultiply);

        /// <summary>
        /// Gets <c>/</c> over <c>BigRat</c>: a zero divisor has no result.
        /// </summary>
        public static Primitive<BigRational, BigRational, BigRational> Divide { get; } =
            BuiltInPrimitive.Partial<BigRational, BigRational, BigRational>("/", static (a, b) => a / b, TryDivide);

        /// <summary>
        /// Gets <c>min</c> over <c>BigRat</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational, BigRational> Min { get; } =
            BuiltInPrimitive.Total<BigRational, BigRational, BigRational>("min", static (a, b) => b < a ? b : a);

        /// <summary>
        /// Gets <c>max</c> over <c>BigRat</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational, BigRational> Max { get; } =
            BuiltInPrimitive.Total<BigRational, BigRational, BigRational>("max", static (a, b) => b < a ? a : b);

        /// <summary>
        /// Gets <c>neg</c> over <c>BigRat</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational> Negate { get; } =
            BuiltInPrimitive.Total<BigRational, BigRational>("neg", static a => -a);

        /// <summary>
        /// Gets <c>abs</c> over <c>BigRat</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational> Abs { get; } =
            BuiltInPrimitive.Total<BigRational, BigRational>("abs", static a => a.Sign < 0 ? -a : a);

        /// <summary>
        /// Gets <c>floor</c>: the greatest integer not above the value.
        /// </summary>
        public static Primitive<BigRational, BigRational> Floor { get; } =
            BuiltInPrimitive.Total<BigRational, BigRational>("floor", FloorOf);

        /// <summary>
        /// Gets <c>ceil</c>: the least integer not below the value.
        /// </summary>
        public static Primitive<BigRational, BigRational> Ceiling { get; } =
            BuiltInPrimitive.Total<BigRational, BigRational>("ceil", CeilingOf);

        /// <summary>
        /// Gets <c>round</c>: the nearest integer, a half rounding away from zero, as num-rational's <c>round</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational> Round { get; } =
            BuiltInPrimitive.Total<BigRational, BigRational>("round", RoundOf);

        /// <summary>
        /// Gets <c>bigrat</c>: the <c>BigRat</c> of a numerator and a denominator, in lowest terms; a zero denominator has
        /// no result, as in egglog 3.0.0.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, BigRational> FromBigIntegers { get; } =
            BuiltInPrimitive.Partial<BigInteger, BigInteger, BigRational>(
                "bigrat", static (a, b) => new BigRational(a, b), TryFromBigIntegers);

        /// <summary>
        /// Gets <c>numer</c>: the numerator in lowest terms, which carries the sign.
        /// </summary>
        public static Primitive<BigRational, BigInteger> Numerator { get; } =
            BuiltInPrimitive.Total<BigRational, BigInteger>("numer", static a => a.Numerator);

        /// <summary>
        /// Gets <c>denom</c>: the denominator in lowest terms, which is positive.
        /// </summary>
        public static Primitive<BigRational, BigInteger> Denominator { get; } =
            BuiltInPrimitive.Total<BigRational, BigInteger>("denom", static a => a.Denominator);

        /// <summary>
        /// Gets <c>to-f64</c>: the nearest <c>f64</c>, a tie rounding to even and a value too large for an <c>f64</c>
        /// rounding to an infinity, as IEEE division does, as num-rational's <c>to_f64</c>.
        /// </summary>
        public static Primitive<BigRational, double> ToDouble { get; } =
            BuiltInPrimitive.Total<BigRational, double>("to-f64", ToDoubleOf);

        /// <summary>
        /// Gets <c>to-i64</c>: the value as an <c>i64</c>; one that is not an integer, or is outside <c>i64</c>, has no
        /// result.
        /// </summary>
        public static Primitive<BigRational, long> ToInt64 { get; } = BuiltInPrimitive.Partial<BigRational, long>("to-i64", TryToInt64);

        /// <summary>
        /// Gets <c>pow</c>: the value raised to an integer power. A power that is not an integer, a negative power of 0,
        /// and, for any other value than 0, a power whose magnitude is above <c>i64::MAX</c> (<c>i64::MIN</c> included)
        /// have no result; 0 to the power 0 is 1, and to any positive power 0. A result whose numerator or denominator is
        /// longer than <c>int.MaxValue</c> bits throws <see cref="OverflowException"/>, which fails the rule, where egglog
        /// runs out of memory.
        /// </summary>
        public static Primitive<BigRational, BigRational, BigRational> Power { get; } =
            BuiltInPrimitive.Partial<BigRational, BigRational, BigRational>("pow", TryPower);

        /// <summary>
        /// Gets <c>log</c>: 0 at 1. egglog's is unfinished (<c>todo!</c>) at any other value, where a call throws
        /// <see cref="NotSupportedException"/>, which fails the rule, as egglog panics.
        /// </summary>
        public static Primitive<BigRational, BigRational> Log { get; } =
            BuiltInPrimitive.Partial<BigRational, BigRational>("log", TryLog);

        /// <summary>
        /// Gets <c>sqrt</c>: the square root of a positive value whose numerator and denominator are perfect squares; any
        /// other value, 0 included, has no result.
        /// </summary>
        public static Primitive<BigRational, BigRational> Sqrt { get; } =
            BuiltInPrimitive.Partial<BigRational, BigRational>("sqrt", TrySqrt);

        /// <summary>
        /// Gets <c>cbrt</c>: 1 at 1. egglog's is unfinished (<c>todo!</c>) at any other value, where a call throws
        /// <see cref="NotSupportedException"/>, which fails the rule, as egglog panics.
        /// </summary>
        public static Primitive<BigRational, BigRational> Cbrt { get; } =
            BuiltInPrimitive.Partial<BigRational, BigRational>("cbrt", TryCbrt);

        /// <summary>
        /// Gets <c>bool-&lt;</c> over <c>BigRat</c>, whose filter is egglog's <c>&lt;</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational, bool> Less { get; } =
            BuiltInPrimitive.Comparison<BigRational>("<", "bool-<", static (a, b) => a < b);

        /// <summary>
        /// Gets <c>bool-&lt;=</c> over <c>BigRat</c>, whose filter is egglog's <c>&lt;=</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational, bool> LessOrEqual { get; } =
            BuiltInPrimitive.Comparison<BigRational>("<=", "bool-<=", static (a, b) => a <= b);

        /// <summary>
        /// Gets <c>bool-&gt;</c> over <c>BigRat</c>, whose filter is egglog's <c>&gt;</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational, bool> Greater { get; } =
            BuiltInPrimitive.Comparison<BigRational>(">", "bool->", static (a, b) => a > b);

        /// <summary>
        /// Gets <c>bool-&gt;=</c> over <c>BigRat</c>, whose filter is egglog's <c>&gt;=</c>.
        /// </summary>
        public static Primitive<BigRational, BigRational, bool> GreaterOrEqual { get; } =
            BuiltInPrimitive.Comparison<BigRational>(">=", "bool->=", static (a, b) => a >= b);

        /// <summary>
        /// num-rational's <c>checked_add</c>, which always has a result over big integers.
        /// </summary>
        static bool TryAdd(BigRational a, BigRational b, out BigRational result)
        {
            result = a + b;
            return true;
        }

        /// <summary>
        /// num-rational's <c>checked_sub</c>, which always has a result over big integers.
        /// </summary>
        static bool TrySubtract(BigRational a, BigRational b, out BigRational result)
        {
            result = a - b;
            return true;
        }

        /// <summary>
        /// num-rational's <c>checked_mul</c>, which always has a result over big integers.
        /// </summary>
        static bool TryMultiply(BigRational a, BigRational b, out BigRational result)
        {
            result = a * b;
            return true;
        }

        /// <summary>
        /// num-rational's <c>checked_div</c>: a zero divisor has no result.
        /// </summary>
        static bool TryDivide(BigRational a, BigRational b, out BigRational result)
        {
            if (b.Sign == 0)
            {
                result = default;
                return false;
            }

            result = a / b;
            return true;
        }

        /// <summary>
        /// egglog 3.0.0's <c>bigrat</c>: a zero denominator has no result.
        /// </summary>
        static bool TryFromBigIntegers(BigInteger numerator, BigInteger denominator, out BigRational result)
        {
            if (denominator.IsZero)
            {
                result = default;
                return false;
            }

            result = new BigRational(numerator, denominator);
            return true;
        }

        /// <summary>
        /// num-rational's <c>floor</c>: a negative value's numerator moves down by the denominator less 1 before the
        /// division, which truncates.
        /// </summary>
        static BigRational FloorOf(BigRational a) => a.Sign < 0
            ? new BigRational((a.Numerator - a.Denominator + 1) / a.Denominator)
            : new BigRational(a.Numerator / a.Denominator);

        /// <summary>
        /// num-rational's <c>ceil</c>: a non-negative value's numerator moves up by the denominator less 1 before the
        /// division, which truncates.
        /// </summary>
        static BigRational CeilingOf(BigRational a) => a.Sign < 0
            ? new BigRational(a.Numerator / a.Denominator)
            : new BigRational((a.Numerator + a.Denominator - 1) / a.Denominator);

        /// <summary>
        /// num-rational's <c>round</c>: the truncation, moved one away from zero when the fraction's magnitude is at least
        /// a half.
        /// </summary>
        static BigRational RoundOf(BigRational a)
        {
            var truncated = BigInteger.DivRem(a.Numerator, a.Denominator, out var remainder);
            if (BigInteger.Abs(remainder) * 2 >= a.Denominator)
                truncated += a.Sign;

            return new BigRational(truncated);
        }

        /// <summary>
        /// egglog's <c>to-i64</c>: an integer within <c>i64</c>.
        /// </summary>
        static bool TryToInt64(BigRational a, out long result)
        {
            if (a.IsInteger && a.Numerator >= long.MinValue && a.Numerator <= long.MaxValue)
            {
                result = (long)a.Numerator;
                return true;
            }

            result = 0;
            return false;
        }

        /// <summary>
        /// egglog's <c>pow</c>: an integer power only; for 0, the power 0 gives 1, a positive one 0 and a negative one
        /// nothing; otherwise a negative power raises the reciprocal, and the power's magnitude must convert to an
        /// <c>i64</c> (num-traits' <c>checked_pow</c> over it).
        /// </summary>
        static bool TryPower(BigRational a, BigRational b, out BigRational result)
        {
            result = default;
            if (b.IsInteger == false)
                return false;

            if (a.Sign == 0)
            {
                if (b.Sign < 0)
                    return false;

                result = b.Sign == 0 ? BigInteger.One : BigInteger.Zero;
                return true;
            }

            var exponent = BigInteger.Abs(b.Numerator);
            if (exponent > long.MaxValue)
                return false;

            // A negative power raises the reciprocal, whose sign stays on the numerator.
            var numerator = b.Sign < 0 ? a.Denominator * a.Sign : a.Numerator;
            var denominator = b.Sign < 0 ? BigInteger.Abs(a.Numerator) : a.Denominator;
            result = BigRational.FromReduced(Raise(numerator, (long)exponent), Raise(denominator, (long)exponent));
            return true;
        }

        /// <summary>
        /// Raises an integer to a power of at most <c>i64</c>, by num-traits' <c>checked_pow</c>: squares while the power
        /// is even, then squares and multiplies, so that each product is allocated at its own size. 1, -1 and 0 take any
        /// power; anything else a power whose result has at most <c>int.MaxValue</c> bits, the most a
        /// <see cref="BigInteger"/> holds on .NET 9 and later, on every runtime.
        /// </summary>
        /// <exception cref="OverflowException">The result is longer than <c>int.MaxValue</c> bits.</exception>
        static BigInteger Raise(BigInteger value, long exponent)
        {
            if (exponent == 0)
                return BigInteger.One;

            if (value.IsZero || value.IsOne)
                return value;

            if (value == BigInteger.MinusOne)
                return exponent % 2 == 0 ? BigInteger.One : BigInteger.MinusOne;

            // |value| >= 2, so the result has floor(exponent * log2 |value|) + 1 bits.
            if (exponent * BigInteger.Log(BigInteger.Abs(value), 2) >= int.MaxValue)
                throw new OverflowException(
                    $"Raising this BigRat to the power {exponent} makes more than int.MaxValue bits, more than a BigInteger holds.");

            var power = value;
            while ((exponent & 1) == 0)
            {
                power *= power;
                exponent >>= 1;
            }

            var result = power;
            while (exponent > 1)
            {
                exponent >>= 1;
                power *= power;
                if ((exponent & 1) == 1)
                    result *= power;
            }

            return result;
        }

        /// <summary>
        /// egglog's <c>log</c>: 0 at 1, unfinished elsewhere.
        /// </summary>
        static bool TryLog(BigRational a, out BigRational result)
        {
            if (a.IsInteger && a.Numerator.IsOne)
            {
                result = BigInteger.Zero;
                return true;
            }

            throw new NotSupportedException("egglog's log of a BigRat is unfinished (todo!) except at 1.");
        }

        /// <summary>
        /// egglog's <c>cbrt</c>: 1 at 1, unfinished elsewhere.
        /// </summary>
        static bool TryCbrt(BigRational a, out BigRational result)
        {
            if (a.IsInteger && a.Numerator.IsOne)
            {
                result = BigInteger.One;
                return true;
            }

            throw new NotSupportedException("egglog's cbrt of a BigRat is unfinished (todo!) except at 1.");
        }

        /// <summary>
        /// egglog's <c>sqrt</c>: of a positive numerator and denominator that are both perfect squares, under num-bigint's
        /// floor square root.
        /// </summary>
        static bool TrySqrt(BigRational a, out BigRational result)
        {
            result = default;
            if (a.Sign <= 0)
                return false;

            var numerator = FloorSqrt(a.Numerator);
            var denominator = FloorSqrt(a.Denominator);
            if (numerator * numerator != a.Numerator || denominator * denominator != a.Denominator)
                return false;

            result = BigRational.FromReduced(numerator, denominator);
            return true;
        }

        /// <summary>
        /// The floor square root of a positive integer, by Newton's method from above.
        /// </summary>
        static BigInteger FloorSqrt(BigInteger value)
        {
            if (value < 4)
                return BigInteger.One;

            var x = BigInteger.One << (int)((value.GetBitLength() + 1) / 2);
            while (true)
            {
                var y = (x + value / x) >> 1;
                if (y >= x)
                    return x;

                x = y;
            }
        }

        /// <summary>
        /// num-rational's <c>to_f64</c> (<c>ratio_to_f64</c>): a quotient of both parts exactly representable is the
        /// division's; otherwise a quotient of 55 or 56 bits, fewer where the result is subnormal, rounded to 53 with
        /// ties to even, its remainder counting, and scaled exactly (<c>ldexp</c>, here <see cref="Math.ScaleB"/>).
        /// </summary>
        static double ToDoubleOf(BigRational a)
        {
            const int MantissaDigits = 53;
            const int MaxExponent = 1024;
            const int MinExponent = -1021;
            const long MaxExactInteger = 1L << MantissaDigits;

            if (a.Sign == 0)
                return 0.0;

            double sign = a.Sign;
            var numerator = BigInteger.Abs(a.Numerator);
            var denominator = a.Denominator;
            if (numerator <= MaxExactInteger && denominator <= MaxExactInteger)
                return (double)a.Numerator / (double)denominator;

            // Filter out overflows and underflows; past these the shift below would not fit.
            var difference = numerator.GetBitLength() - denominator.GetBitLength();
            if (difference > MaxExponent)
                return double.PositiveInfinity * sign;

            if (difference < -(-MinExponent + MantissaDigits + 1))
                return 0.0 * sign;

            var shift = Math.Max(difference, MinExponent) - MantissaDigits - 2;
            if (shift >= 0)
                denominator <<= (int)shift;
            else
                numerator <<= (int)-shift;

            var quotient = (ulong)BigInteger.DivRem(numerator, denominator, out var remainder);
            var quotientBits = 64 - BitOperations.LeadingZeroCount(quotient);
            var subnormalBits = MinExponent - shift;
            var roundingBits = (int)(Math.Max(quotientBits, subnormalBits) - MantissaDigits);
            var roundingMask = (1UL << roundingBits) - 1;
            var lowBit = (quotient & (1UL << roundingBits)) != 0;
            var highRoundingBit = (quotient & (1UL << (roundingBits - 1))) != 0;
            var lowRoundingBits = (quotient & (roundingMask >> 1)) != 0;
            if (highRoundingBit && (lowBit || lowRoundingBits || remainder.IsZero == false))
                quotient += 1UL << roundingBits;

            quotient &= ~roundingMask;
            // The rounding bits stay in place, cleared, so the quotient is exact and scales by the shift alone.
            return Math.ScaleB(quotient * sign, (int)shift);
        }

    }

}
