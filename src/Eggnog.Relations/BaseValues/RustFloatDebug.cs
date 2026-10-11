using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Formats <see cref="double"/> and <see cref="float"/> exactly as Rust's <c>Debug</c> for <c>f64</c> and
    /// <c>f32</c> does, for <see cref="BaseValuePrinter"/>. A port of <c>core::fmt::float</c>'s
    /// <c>float_to_general_debug</c> and of the <c>core::num::flt2dec</c> pieces it uses: <c>decoder::decode</c>,
    /// <c>strategy::dragon::format_shortest</c> (Rust runs Grisu first, but Grisu only answers when it agrees with
    /// Dragon, so Dragon defines the output), <c>digits_to_dec_str</c> and <c>digits_to_exp_str</c>.
    /// </summary>
    /// <remarks>
    /// The digits are the shortest that read back as the same float. When two candidates of that length are equally
    /// near the exact value, Rust takes the larger magnitude (Dragon's <c>2 * mant &gt;= scale</c>); .NET's round-trip
    /// format takes the even digit, which is why this does not use it. Cold path: it works in
    /// <see cref="BigInteger"/> and allocates.
    /// </remarks>
    internal static class RustFloatDebug
    {

        /// <summary>
        /// <c>floor(2^32 * log10(2))</c>, Rust's <c>estimate_scaling_factor</c> constant.
        /// </summary>
        const long Log10Of2Times2To32 = 1292913986L;

        /// <summary>
        /// Formats an <c>f64</c> as Rust's <c>Debug</c> does: <c>NaN</c>, <c>inf</c>, <c>-inf</c>, <c>0.0</c> and
        /// <c>-0.0</c> for the special values; otherwise the shortest round-trip digits, in exponential form
        /// (<c>1e16</c>, <c>1.5e-7</c>) when the magnitude is below <c>1e-4</c> or at least <c>1e16</c>, and in decimal
        /// form with at least one fractional digit (<c>1.0</c>, <c>0.0001</c>) otherwise.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        public static string Format(double value)
        {
            if (double.IsNaN(value))
                return "NaN";

            var sign = double.IsNegative(value) ? "-" : "";
            if (double.IsInfinity(value))
                return sign + "inf";

            if (value == 0)
                return sign + "0.0";

            // core::num::flt2dec::decoder::decode over f64's integer_decode.
            var bits = BitConverter.DoubleToUInt64Bits(value);
            var biased = (int)((bits >> 52) & 0x7FF);
            var fraction = bits & 0xF_FFFF_FFFF_FFFFUL;
            var abs = Math.Abs(value);
            var exponential = abs < 1e-4 || abs >= 1e16;
            if (biased == 0)
                return FormatSubnormal(sign, exponential, fraction << 1, biased - 1075);

            return FormatNormal(sign, exponential, fraction | (1UL << 52), biased - 1075, fraction == 0);
        }

        /// <summary>
        /// Formats an <c>f32</c> as Rust's <c>Debug</c> does: as <see cref="Format(double)"/>, with <c>f32</c>'s
        /// shortest digits and its thresholds (<c>1e-4f32</c> and <c>1e16f32</c>, compared in single precision).
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        public static string Format(float value)
        {
            if (float.IsNaN(value))
                return "NaN";

            var sign = float.IsNegative(value) ? "-" : "";
            if (float.IsInfinity(value))
                return sign + "inf";

            if (value == 0)
                return sign + "0.0";

            var bits = BitConverter.SingleToUInt32Bits(value);
            var biased = (int)((bits >> 23) & 0xFF);
            var fraction = (ulong)(bits & 0x7F_FFFFu);
            var abs = Math.Abs(value);
            var exponential = abs < 1e-4f || abs >= 1e16f;
            if (biased == 0)
                return FormatSubnormal(sign, exponential, fraction << 1, biased - 150);

            return FormatNormal(sign, exponential, fraction | (1UL << 23), biased - 150, fraction == 0);
        }

        /// <summary>
        /// Formats a subnormal float given as <c>mantissa * 2^exponent</c> (integer_decode's form, mantissa already
        /// shifted left once): <c>decoder::decode</c>, shortest digits and layout.
        /// </summary>
        static string FormatSubnormal(string sign, bool exponential, ulong mantissa, int exponent)
        {
            // Subnormal: neighbors (mant - 2, exp) -- (mant, exp) -- (mant + 2, exp). integer_decode shifts a subnormal's
            // mantissa left once, so Rust's `even` test always passes and the bounds are always inclusive.
            var digits = FormatShortest(mantissa, 1, 1, exponent, (mantissa & 1) == 0, out var k);
            return Layout(sign, exponential, digits, k);
        }

        /// <summary>
        /// Formats a normal float given as <c>mantissa * 2^exponent</c> (integer_decode's form, the hidden bit set):
        /// <c>decoder::decode</c>, shortest digits and layout.
        /// </summary>
        static string FormatNormal(string sign, bool exponential, ulong mantissa, int exponent, bool powerOfTwo)
        {
            var inclusive = (mantissa & 1) == 0;

            // A zero fraction (Rust compares the mantissa with the minimum normal's) has a lower neighbor half as far
            // away: (maxmant, exp - 1) -- (minnormmant, exp) -- (minnormmant + 1, exp).
            var digits = powerOfTwo
                ? FormatShortest(mantissa << 2, 1, 2, exponent - 2, inclusive, out var k)
                : FormatShortest(mantissa << 1, 1, 1, exponent - 1, inclusive, out k);

            return Layout(sign, exponential, digits, k);
        }

        /// <summary>
        /// Lays out digits <c>0.d1d2... * 10^k</c> as <c>digits_to_exp_str</c> (no minimum digit count, lower-case
        /// <c>e</c>) or as <c>digits_to_dec_str</c> with one minimum fractional digit.
        /// </summary>
        static string Layout(string sign, bool exponential, string digits, int k)
        {
            var builder = new StringBuilder(sign, digits.Length + 8);
            if (exponential)
            {
                builder.Append(digits[0]);
                if (digits.Length > 1)
                    builder.Append('.').Append(digits, 1, digits.Length - 1);

                return builder.Append('e').Append((k - 1).ToString(CultureInfo.InvariantCulture)).ToString();
            }

            if (k <= 0)
                return builder.Append("0.").Append('0', -k).Append(digits).ToString();

            if (k < digits.Length)
                return builder.Append(digits, 0, k).Append('.').Append(digits, k, digits.Length - k).ToString();

            return builder.Append(digits).Append('0', k - digits.Length).Append(".0").ToString();
        }

        /// <summary>
        /// Rust's <c>dragon::format_shortest</c>: the shortest digits <c>d</c> and exponent <paramref name="k"/> with
        /// <c>0.d * 10^k</c> strictly (or, when <paramref name="inclusive"/>, weakly) inside
        /// <c>((mant - minus) * 2^exp, (mant + plus) * 2^exp)</c>, nearest to <c>mant * 2^exp</c>, ties rounding up.
        /// </summary>
        /// <param name="decodedMantissa">The decoded mantissa <c>mant</c>; nonzero.</param>
        /// <param name="decodedMinus">The distance <c>minus</c> to the lower bound, in units of <c>2^exp</c>.</param>
        /// <param name="decodedPlus">The distance <c>plus</c> to the upper bound, in units of <c>2^exp</c>.</param>
        /// <param name="exponent">The binary exponent <c>exp</c>.</param>
        /// <param name="inclusive">Whether the bounds themselves read back as the value.</param>
        /// <param name="k">The decimal exponent of the digits.</param>
        /// <returns>The digits, the first nonzero.</returns>
        static string FormatShortest(ulong decodedMantissa, ulong decodedMinus, ulong decodedPlus, int exponent, bool inclusive, out int k)
        {
            // estimate_scaling_factor(mant + plus, exp): k_0 with 10^(k_0 - 1) < high <= 10^(k_0 + 1), never above the
            // tight bound.
            var nbits = 64 - BitOperations.LeadingZeroCount(decodedMantissa + decodedPlus - 1);
            k = (int)(((nbits + exponent) * Log10Of2Times2To32) >> 32);

            // v = mant / scale, low = (mant - minus) / scale, high = (mant + plus) / scale.
            var mant = new BigInteger(decodedMantissa);
            var minus = new BigInteger(decodedMinus);
            var plus = new BigInteger(decodedPlus);
            var scale = BigInteger.One;
            if (exponent < 0)
            {
                scale <<= -exponent;
            }
            else
            {
                mant <<= exponent;
                minus <<= exponent;
                plus <<= exponent;
            }

            // Divide by 10^k: now scale / 10 < mant + plus <= scale * 10.
            if (k >= 0)
            {
                scale *= BigInteger.Pow(10, k);
            }
            else
            {
                var power = BigInteger.Pow(10, -k);
                mant *= power;
                minus *= power;
                plus *= power;
            }

            // Fix up so that scale < mant + plus <= scale * 10 (multiplying scale by 10 is skipped by not multiplying
            // the others by 10 for the first digit).
            if (Below(scale, mant + plus, inclusive))
            {
                k++;
            }
            else
            {
                mant *= 10;
                minus *= 10;
                plus *= 10;
            }

            var digits = new StringBuilder(17);
            bool down;
            bool up;
            while (true)
            {
                // The next digit is floor(mant / scale) < 10.
                var digit = BigInteger.DivRem(mant, scale, out mant);
                digits.Append((char)('0' + (int)digit));

                // Stop and round down (keep the digits) when the remainder is within the lower margin; stop and round
                // up (increment the last digit) when the next digit up is within the upper margin.
                down = Below(mant, minus, inclusive);
                up = Below(scale, mant + plus, inclusive);
                if (down || up)
                    break;

                mant *= 10;
                minus *= 10;
                plus *= 10;
            }

            // When both are possible, round to the nearer; a tie rounds up.
            if (up && (down == false || mant * 2 >= scale))
            {
                // round_up: 1299 -> 1300; 999 -> 1000 with the exponent one higher.
                var last = digits.Length - 1;
                while (last >= 0 && digits[last] == '9')
                    last--;

                if (last >= 0)
                {
                    digits[last]++;
                    for (var i = last + 1; i < digits.Length; i++)
                        digits[i] = '0';
                }
                else
                {
                    digits[0] = '1';
                    for (var i = 1; i < digits.Length; i++)
                        digits[i] = '0';

                    digits.Append('0');
                    k++;
                }
            }

            return digits.ToString();
        }

        /// <summary>
        /// Rust's <c>a.cmp(&amp;b) &lt; rounding</c>: <c>a &lt;= b</c> when the bounds are inclusive, else <c>a &lt; b</c>.
        /// </summary>
        static bool Below(BigInteger a, BigInteger b, bool inclusive) => inclusive ? a <= b : a < b;

    }

}
