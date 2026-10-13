using System.Globalization;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Formats strings and characters exactly as Rust's <c>Debug</c> for <c>str</c> and <c>char</c> does, for
    /// <see cref="BaseValuePrinter"/>: a port of <c>impl Debug for str</c>, <c>impl Debug for char</c> and
    /// <c>char::escape_debug_ext</c> (core/src/fmt/mod.rs, core/src/char/methods.rs).
    /// </summary>
    /// <remarks>
    /// Each code point is written as <c>\0</c>, <c>\t</c>, <c>\r</c>, <c>\n</c> or <c>\\</c>; the delimiting quote
    /// (<c>"</c> in a string, <c>'</c> in a character) as <c>\"</c> or <c>\'</c>; any code point in
    /// <see cref="RustDebugEscapes"/> (unprintable or grapheme-extending) as <c>\u{hex}</c> in lower-case hex; and every
    /// other code point as itself. Rust strings are always valid UTF-8; a .NET string may hold a lone surrogate, which is
    /// written as <c>\u{hex}</c> like any other unprintable code point. Cold path: allocates.
    /// </remarks>
    internal static class RustStrDebug
    {

        /// <summary>
        /// Formats a string as Rust's <c>Debug</c> for <c>str</c> does: in double quotes, escaping per code point.
        /// </summary>
        /// <param name="value">The string.</param>
        /// <returns>The text.</returns>
        public static string Format(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                int codePoint = value[i];
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    codePoint = char.ConvertToUtf32(value[i], value[i + 1]);
                    i++;
                }

                Append(builder, codePoint, '"');
            }

            return builder.Append('"').ToString();
        }

        /// <summary>
        /// Formats a UTF-16 code unit as Rust's <c>Debug</c> for <c>char</c> does: in single quotes, escaped as in
        /// <see cref="Format(string)"/> except that <c>'</c> rather than <c>"</c> is escaped. A surrogate code unit is
        /// written as <c>\u{hex}</c>.
        /// </summary>
        /// <param name="value">The character.</param>
        /// <returns>The text.</returns>
        public static string Format(char value) => Format((int)value);

        /// <summary>
        /// Formats a scalar value as Rust's <c>Debug</c> for <c>char</c> does (<see cref="Rune"/> is the exact analog
        /// of Rust's <c>char</c>).
        /// </summary>
        /// <param name="value">The scalar value.</param>
        /// <returns>The text.</returns>
        public static string Format(Rune value) => Format(value.Value);

        /// <summary>
        /// Formats a code point in single quotes.
        /// </summary>
        static string Format(int codePoint)
        {
            var builder = new StringBuilder(4);
            builder.Append('\'');
            Append(builder, codePoint, '\'');
            return builder.Append('\'').ToString();
        }

        /// <summary>
        /// Appends one code point as <c>char::escape_debug_ext</c> with <c>escape_grapheme_extended</c> set and only
        /// <paramref name="quote"/> of the two quotes escaped.
        /// </summary>
        /// <param name="builder">The output.</param>
        /// <param name="codePoint">A code point in <c>[0, 0x10FFFF]</c>; surrogates are escaped.</param>
        /// <param name="quote">The delimiting quote: <c>"</c> or <c>'</c>.</param>
        static void Append(StringBuilder builder, int codePoint, char quote)
        {
            switch (codePoint)
            {
                case '\0':
                    builder.Append("\\0");
                    return;
                case '\t':
                    builder.Append("\\t");
                    return;
                case '\r':
                    builder.Append("\\r");
                    return;
                case '\n':
                    builder.Append("\\n");
                    return;
                case '\\':
                    builder.Append("\\\\");
                    return;
            }

            if (codePoint == quote)
            {
                builder.Append('\\').Append(quote);
                return;
            }

            if (RustDebugEscapes.IsEscaped(codePoint))
            {
                builder.Append("\\u{").Append(codePoint.ToString("x", CultureInfo.InvariantCulture)).Append('}');
                return;
            }

            if (codePoint < 0x10000)
            {
                builder.Append((char)codePoint);
                return;
            }

            // A printable supplementary code point: its surrogate pair.
            var offset = codePoint - 0x10000;
            builder.Append((char)(0xD800 + (offset >> 10))).Append((char)(0xDC00 + (offset & 0x3FF)));
        }

    }

}
