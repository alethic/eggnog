using System;
using System.Globalization;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Renders base values as egglog prints extracted literals. egglog's literal sorts reconstruct
    /// <c>Literal::String</c> and <c>Literal::Float</c> (src/sort/string.rs:38-47, src/sort/f64.rs:54-63), and
    /// <c>TermDag::to_string</c> prints them with <c>Literal</c>'s <c>Display</c>
    /// (egglog-ast/src/generic_ast_helpers.rs:740-758): a string raw between double quotes, with no escaping, and a float
    /// with <c>f64</c>'s <c>Display</c> (never exponential) plus <c>.0</c> when that text parses as an <c>i64</c>.
    /// Integers, booleans and unit print the same under <c>Display</c> and <c>Debug</c>, so their codecs' formats serve.
    /// </summary>
    /// <remarks>
    /// Cold path: called once per distinct literal an extraction builds; it allocates the text.
    /// </remarks>
    internal static class LiteralText
    {

        /// <summary>
        /// Renders a string as egglog's <c>Literal::String</c> displays it: between double quotes, unescaped.
        /// </summary>
        /// <param name="value">The string.</param>
        /// <returns>The text.</returns>
        public static string String(string value) => "\"" + value + "\"";

        /// <summary>
        /// Renders a float as egglog's <c>Literal::Float</c> displays it: Rust's <c>Display</c> for <c>f64</c> (the
        /// shortest round-trip digits, laid out in positional notation, never with an exponent; <c>NaN</c>, <c>inf</c>,
        /// <c>-inf</c>, and a sign on negative zero), followed by <c>.0</c> when that text parses as an <c>i64</c>. So
        /// <c>1.0</c> prints as <c>1.0</c>, <c>1e20</c> as <c>100000000000000000000</c> (too large for an <c>i64</c>)
        /// and <c>1e-7</c> as <c>0.0000001</c>.
        /// </summary>
        /// <remarks>
        /// The digits are <see cref="RustFloatDebug"/>'s, which <c>Display</c> shares with <c>Debug</c>; only the layout
        /// differs. <c>Debug</c> uses positional notation with at least one fractional digit for magnitudes in
        /// <c>[1e-4, 1e16)</c>, and there its text is already egglog's: an integral value's <c>Display</c> parses as an
        /// <c>i64</c> (it is below <c>1e16</c>) and so regains the <c>.0</c>, and a fractional one is unchanged.
        /// Outside that range <c>Debug</c> is exponential, and the digits are laid out again as <c>Display</c>'s
        /// <c>digits_to_dec_str</c> with no minimum fractional digits does. Shortest digits never end in a zero, so the
        /// layout is unambiguous.
        /// </remarks>
        /// <param name="value">The float.</param>
        /// <returns>The text.</returns>
        public static string Float(double value)
        {
            var debug = RustFloatDebug.Format(value);
            var e = debug.IndexOf('e');
            if (e < 0)
                return debug;

            // Debug's exponential form: [-]d[.ddd]e[-]x, the value 0.dddd * 10^(x + 1).
            var negative = debug[0] == '-';
            var digits = debug.Substring(negative ? 1 : 0, e - (negative ? 1 : 0)).Replace(".", "", StringComparison.Ordinal);

            var point = int.Parse(debug.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) + 1;
            var builder = new StringBuilder(digits.Length + Math.Abs(point) + 3);
            if (negative)
                builder.Append('-');

            if (point <= 0)
                builder.Append("0.").Append('0', -point).Append(digits);
            else if (point < digits.Length)
                builder.Append(digits, 0, point).Append('.').Append(digits, point, digits.Length - point);
            else
                builder.Append(digits).Append('0', point - digits.Length);

            var display = builder.ToString();
            return long.TryParse(display, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ? display + ".0" : display;
        }

    }

}
