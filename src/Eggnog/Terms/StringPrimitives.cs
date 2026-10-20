using System;
using System.Text;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>String</c> primitives, with egglog's names and Rust's <c>str</c> semantics. Matching is ordinal, by
    /// code unit. egglog's <c>+</c> takes any number of arguments; here <see cref="Concat"/> takes two, and more nest.
    /// </summary>
    public static class StringPrimitives
    {

        /// <summary>
        /// Gets <c>+</c> over <c>String</c>: the concatenation of two.
        /// </summary>
        public static Primitive<string, string, string> Concat { get; } =
            BuiltInPrimitive.Total<string, string, string>("+", static (a, b) => string.Concat(a, b));

        /// <summary>
        /// Gets <c>replace</c>: the first string with every match of the second, left to right and not overlapping,
        /// replaced by the third (Rust's <c>str::replace</c>). An empty pattern matches before every character and at
        /// the end, so <c>replace "ab" "" "-"</c> is <c>-a-b-</c>.
        /// </summary>
        public static Primitive<string, string, string, string> Replace { get; } =
            BuiltInPrimitive.Total<string, string, string, string>("replace", ReplaceAll);

        /// <summary>
        /// Gets <c>count-matches</c>: the number of matches of the second string in the first, left to right and not
        /// overlapping (Rust's <c>str::matches</c>). An empty pattern matches before every character and at the end, so it
        /// counts one more than the characters.
        /// </summary>
        public static Primitive<string, string, long> CountMatches { get; } =
            BuiltInPrimitive.Total<string, string, long>("count-matches", Count);

        /// <summary>
        /// Rust's <c>str::replace</c>. .NET's <see cref="string.Replace(string, string)"/> refuses an empty pattern, which
        /// Rust matches at every character boundary: before each character (a surrogate pair is one) and at the end.
        /// </summary>
        static string ReplaceAll(string text, string pattern, string replacement)
        {
            if (pattern.Length > 0)
                return text.Replace(pattern, replacement, StringComparison.Ordinal);

            var result = new StringBuilder(text.Length + (text.Length + 1) * replacement.Length);
            result.Append(replacement);
            for (var i = 0; i < text.Length; i++)
            {
                result.Append(text[i]);
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    result.Append(text[++i]);

                result.Append(replacement);
            }

            return result.ToString();
        }

        /// <summary>
        /// Rust's <c>str::matches(pattern).count()</c>.
        /// </summary>
        static long Count(string text, string pattern)
        {
            if (pattern.Length == 0)
            {
                long boundaries = 1;
                for (var i = 0; i < text.Length; i++)
                {
                    if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        i++;

                    boundaries++;
                }

                return boundaries;
            }

            long count = 0;
            for (var at = text.IndexOf(pattern, StringComparison.Ordinal); at >= 0; at = text.IndexOf(pattern, at + pattern.Length, StringComparison.Ordinal))
                count++;

            return count;
        }

    }

}
