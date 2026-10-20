using System;
using System.Globalization;
using System.Numerics;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>BigInt</c> primitives over <see cref="BigInteger"/>, with egglog's names.
    /// </summary>
    /// <remarks>
    /// <see cref="Divide"/>, <see cref="Remainder"/>, <see cref="ShiftLeft"/>, <see cref="ShiftRight"/> and
    /// <see cref="Parse"/> are partial where egglog 3.0.0's are (<see cref="Primitive.IsPartial"/>): a zero divisor, a
    /// shift count outside 0 to <c>u32::MAX</c> and text that is not an integer have no result. No result drops the match
    /// in a rule body and fails the rule in a head; a partial primitive's <c>Function</c> throws there,
    /// <see cref="DivideByZeroException"/> for a zero divisor, as <see cref="BigInteger"/>'s own division does, and
    /// <see cref="InvalidOperationException"/> otherwise. Division truncates toward zero, the remainder takes the dividend's
    /// sign, and the bitwise operators and shifts act on the two's complement, as in egglog (num-bigint). The comparisons
    /// return a <c>bool</c>; as a filter (<see cref="RuleBuilder.Where(Term{bool})"/>) they are egglog's <c>&lt;</c>
    /// family.
    /// </remarks>
    public static class BigIntegerPrimitives
    {

        /// <summary>
        /// Gets <c>bigint</c>: the <c>BigInt</c> of an <c>i64</c>.
        /// </summary>
        public static Primitive<long, BigInteger> FromInt64 { get; } = BuiltInPrimitive.Total<long, BigInteger>("bigint", static a => a);

        /// <summary>
        /// Gets <c>+</c> over <c>BigInt</c>.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, BigInteger> Add { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, BigInteger>("+", BigInteger.Add);

        /// <summary>
        /// Gets <c>-</c> over <c>BigInt</c>.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, BigInteger> Subtract { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, BigInteger>("-", BigInteger.Subtract);

        /// <summary>
        /// Gets <c>*</c> over <c>BigInt</c>.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, BigInteger> Multiply { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, BigInteger>("*", BigInteger.Multiply);

        /// <summary>
        /// Gets <c>/</c> over <c>BigInt</c>, truncating toward zero: partial, like egglog's; a zero divisor has no
        /// result.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, BigInteger> Divide { get; } =
            BuiltInPrimitive.Partial<BigInteger, BigInteger, BigInteger>("/", BigInteger.Divide, TryDivide);

        /// <summary>
        /// Gets <c>%</c> over <c>BigInt</c>, with the dividend's sign: partial, like egglog's; a zero divisor has no
        /// result.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, BigInteger> Remainder { get; } =
            BuiltInPrimitive.Partial<BigInteger, BigInteger, BigInteger>("%", BigInteger.Remainder, TryRemainder);

        /// <summary>
        /// Gets <c>&amp;</c> over <c>BigInt</c>: bitwise and.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, BigInteger> BitwiseAnd { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, BigInteger>("&", static (a, b) => a & b);

        /// <summary>
        /// Gets <c>|</c> over <c>BigInt</c>: bitwise or.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, BigInteger> BitwiseOr { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, BigInteger>("|", static (a, b) => a | b);

        /// <summary>
        /// Gets <c>^</c> over <c>BigInt</c>: bitwise exclusive or.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, BigInteger> BitwiseXor { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, BigInteger>("^", static (a, b) => a ^ b);

        /// <summary>
        /// Gets <c>&lt;&lt;</c> over <c>BigInt</c> by an <c>i64</c> count: partial, like egglog 3.0.0's; a count outside 0
        /// to <c>u32::MAX</c> has no result. A result longer than <c>int.MaxValue</c> bits throws
        /// <see cref="OverflowException"/>, which fails the rule, on every runtime: no <see cref="BigInteger"/> of .NET 9
        /// and later holds it.
        /// </summary>
        public static Primitive<BigInteger, long, BigInteger> ShiftLeft { get; } =
            BuiltInPrimitive.Partial<BigInteger, long, BigInteger>("<<", TryShiftLeft);

        /// <summary>
        /// Gets <c>&gt;&gt;</c> over <c>BigInt</c> by an <c>i64</c> count, rounding toward negative infinity: partial,
        /// like egglog 3.0.0's; a count outside 0 to <c>u32::MAX</c> has no result.
        /// </summary>
        public static Primitive<BigInteger, long, BigInteger> ShiftRight { get; } =
            BuiltInPrimitive.Partial<BigInteger, long, BigInteger>(">>", TryShiftRight);

        /// <summary>
        /// Gets <c>not-Z</c>: bitwise complement, <c>-a - 1</c>.
        /// </summary>
        public static Primitive<BigInteger, BigInteger> BitwiseNot { get; } = BuiltInPrimitive.Total<BigInteger, BigInteger>("not-Z", static a => ~a);

        /// <summary>
        /// Gets <c>bits</c>: the number of bits of the magnitude, 0 for 0 (num-bigint's <c>bits</c>).
        /// </summary>
        public static Primitive<BigInteger, BigInteger> Bits { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger>("bits", static a => BigInteger.Abs(a).GetBitLength());

        /// <summary>
        /// Gets <c>min</c> over <c>BigInt</c>.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, BigInteger> Min { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, BigInteger>("min", BigInteger.Min);

        /// <summary>
        /// Gets <c>max</c> over <c>BigInt</c>.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, BigInteger> Max { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, BigInteger>("max", BigInteger.Max);

        /// <summary>
        /// Gets <c>bool-=</c> over <c>BigInt</c>: whether the two are equal, as a <c>bool</c>.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, bool> Equal { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, bool>("bool-=", static (a, b) => a == b);

        /// <summary>
        /// Gets <c>&lt;</c> over <c>BigInt</c>.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, bool> Less { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, bool>("<", static (a, b) => a < b);

        /// <summary>
        /// Gets <c>&lt;=</c> over <c>BigInt</c>.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, bool> LessOrEqual { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, bool>("<=", static (a, b) => a <= b);

        /// <summary>
        /// Gets <c>&gt;</c> over <c>BigInt</c>.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, bool> Greater { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, bool>(">", static (a, b) => a > b);

        /// <summary>
        /// Gets <c>&gt;=</c> over <c>BigInt</c>.
        /// </summary>
        public static Primitive<BigInteger, BigInteger, bool> GreaterOrEqual { get; } =
            BuiltInPrimitive.Total<BigInteger, BigInteger, bool>(">=", static (a, b) => a >= b);

        /// <summary>
        /// Gets <c>to-string</c> over <c>BigInt</c>: the decimal digits, with a leading <c>-</c> when negative.
        /// </summary>
        public static Primitive<BigInteger, string> Format { get; } =
            BuiltInPrimitive.Total<BigInteger, string>("to-string", static a => a.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Gets <c>from-string</c>: the <c>BigInt</c> a decimal text spells, as num-bigint parses it: an optional
        /// <c>+</c> or <c>-</c>, then ASCII digits, with <c>_</c> allowed after the first digit. Partial: any other text
        /// has no result.
        /// </summary>
        public static Primitive<string, BigInteger> Parse { get; } = BuiltInPrimitive.Partial<string, BigInteger>("from-string", TryParse);

        /// <summary>
        /// num-bigint's <c>/</c> under egglog's zero check.
        /// </summary>
        static bool TryDivide(BigInteger a, BigInteger b, out BigInteger result)
        {
            if (b.IsZero)
            {
                result = default;
                return false;
            }

            result = BigInteger.Divide(a, b);
            return true;
        }

        /// <summary>
        /// num-bigint's <c>%</c> under egglog's zero check.
        /// </summary>
        static bool TryRemainder(BigInteger a, BigInteger b, out BigInteger result)
        {
            if (b.IsZero)
            {
                result = default;
                return false;
            }

            result = BigInteger.Remainder(a, b);
            return true;
        }

        /// <summary>
        /// egglog's <c>&lt;&lt;</c>: the count converts to <c>u32</c>.
        /// </summary>
        static bool TryShiftLeft(BigInteger a, long b, out BigInteger result)
        {
            if (b < 0 || b > uint.MaxValue)
            {
                result = default;
                return false;
            }

            if (a.IsZero)
            {
                result = a;
                return true;
            }

            if (BigInteger.Abs(a).GetBitLength() + b > int.MaxValue)
                throw new OverflowException($"Shifting this BigInt left by {b} bits makes more than int.MaxValue bits, more than a BigInteger holds.");

            result = a << (int)b;
            return true;
        }

        /// <summary>
        /// egglog's <c>&gt;&gt;</c>: the count converts to <c>u32</c>. A count beyond <c>int.MaxValue</c> shifts out
        /// every bit a <see cref="BigInteger"/> can hold, leaving the sign: 0 or -1.
        /// </summary>
        static bool TryShiftRight(BigInteger a, long b, out BigInteger result)
        {
            if (b < 0 || b > uint.MaxValue)
            {
                result = default;
                return false;
            }

            result = b > int.MaxValue ? (a.Sign < 0 ? BigInteger.MinusOne : BigInteger.Zero) : a >> (int)b;
            return true;
        }

        /// <summary>
        /// num-bigint's <c>BigInt::from_str_radix(text, 10)</c>: a <c>-</c> not followed by <c>+</c> is the sign; then a
        /// <c>+</c> not followed by <c>+</c> is dropped; the rest must be nonempty, must not start with <c>_</c>, and
        /// must hold only ASCII digits and <c>_</c>, which is skipped.
        /// </summary>
        static bool TryParse(string text, out BigInteger result)
        {
            result = default;
            var s = text.AsSpan();
            var negative = false;
            if (s.Length > 0 && s[0] == '-')
            {
                negative = true;
                if (s.Length == 1 || s[1] != '+')
                    s = s[1..];
            }

            if (s.Length > 0 && s[0] == '+' && (s.Length == 1 || s[1] != '+'))
                s = s[1..];

            if (s.Length == 0 || s[0] == '_')
                return false;

            var digits = new char[s.Length];
            var count = 0;
            foreach (var c in s)
            {
                if (c == '_')
                    continue;

                if (c is < '0' or > '9')
                    return false;

                digits[count++] = c;
            }

            var magnitude = BigInteger.Parse(digits.AsSpan(0, count), NumberStyles.None, CultureInfo.InvariantCulture);
            result = negative ? -magnitude : magnitude;
            return true;
        }

    }

}
