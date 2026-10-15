using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="string"/> base values (Rust <c>String</c> and <c>&amp;'static str</c>, and the egglog
    /// frontend's <c>S = Boxed&lt;String&gt;</c>): never inline; every string is interned and stored as its intern index
    /// (core-relations/src/base_values/mod.rs:44-45). Strings are compared ordinally, by UTF-16 code unit. Rust orders
    /// <c>str</c> by UTF-8 byte, which is code-point order; the two orders differ only when a supplementary character
    /// (above U+FFFF, a surrogate pair in UTF-16) meets a character in U+E000-U+FFFF, which UTF-16 orders after it and
    /// Rust before it.
    /// </summary>
    internal readonly struct StringCodec : IBaseValueCodec<string>
    {

        /// <inheritdoc />
        public static bool MayUnbox => false;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(string value, out Value boxed)
        {
            boxed = default;
            return false;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, [MaybeNullWhen(false)] out string value)
        {
            value = null;
            return false;
        }

        /// <summary>
        /// Formats the value exactly as Rust's <c>Debug</c> for <c>str</c> does: in double quotes, with <c>\0</c>,
        /// <c>\t</c>, <c>\r</c>, <c>\n</c>, <c>\\</c> and <c>\"</c> escaped, every unprintable or grapheme-extending
        /// code point (by Rust's Unicode tables) written as <c>\u{hex}</c>, and everything else raw. A lone surrogate,
        /// which a Rust string cannot hold, is written as <c>\u{hex}</c>. See <see cref="RustStrDebug"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        public static string Format(string value) => RustStrDebug.Format(value);

    }

}
