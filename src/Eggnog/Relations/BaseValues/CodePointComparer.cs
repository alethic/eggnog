using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Compares strings by Unicode code point, as Rust's <c>str</c> ordering does, where an ordinal comparison of UTF-16
    /// code units would put U+E000-U+FFFF after the supplementary characters. The two agree everywhere else, so the
    /// comparison is ordinal until the first differing pair of code units, and when both are at or above U+D800 it
    /// moves the surrogates above U+E000-U+FFFF before comparing them (ICU's code point order for UTF-16). Thread-safe;
    /// allocation-free.
    /// </summary>
    internal sealed class CodePointComparer : IComparer<string>
    {

        /// <summary>
        /// Gets the comparer.
        /// </summary>
        public static CodePointComparer Instance { get; } = new();

        CodePointComparer()
        {
        }

        /// <inheritdoc />
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
                return 0;

            if (x is null)
                return -1;

            if (y is null)
                return 1;

            var length = Math.Min(x.Length, y.Length);
            for (var i = 0; i < length; i++)
            {
                int a = x[i];
                int b = y[i];
                if (a == b)
                    continue;

                if (a >= 0xD800 && b >= 0xD800)
                {
                    a = Fixup(a);
                    b = Fixup(b);
                }

                return a - b;
            }

            return x.Length - y.Length;
        }

        /// <summary>
        /// Moves a code unit at or above U+D800 into code point order: U+E000-U+FFFF down to U+D800-U+F7FF, and the
        /// surrogates, which encode U+10000 and above, up to U+F800-U+FFFF.
        /// </summary>
        static int Fixup(int unit) => unit >= 0xE000 ? unit - 0x800 : unit + 0x2000;

    }

}
