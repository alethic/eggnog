using System.Text;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Pins <see cref="RustStrDebug"/> to Rust's <c>Debug</c> for <c>str</c> and <c>char</c>. Every expected string was
    /// printed by rustc 1.99 (Unicode 17.0.0), not derived by hand.
    /// </summary>
    public class RustStrDebugTests
    {

        /// <summary>
        /// One code point as a <c>char</c> and as a one-character <c>str</c>. Covers a code point newer than .NET's
        /// Unicode tables (U+088F, printed raw although .NET calls it unassigned), a spacing mark that extends graphemes
        /// (U+09BE), a halfwidth sound mark Rust prints raw (U+FF9E), a variation selector (U+E0100), a noncharacter
        /// (U+10FFFF) and a supplementary emoji.
        /// </summary>
        [Theory]
        [InlineData(0x88F, "'\u088f'", "\"\u088f\"")]
        [InlineData(0x9BE, "'\\u{9be}'", "\"\\u{9be}\"")]
        [InlineData(0xFF9E, "'\uff9e'", "\"\uff9e\"")]
        [InlineData(0x200C, "'\\u{200c}'", "\"\\u{200c}\"")]
        [InlineData(0xE0100, "'\\u{e0100}'", "\"\\u{e0100}\"")]
        [InlineData(0x10FFFF, "'\\u{10ffff}'", "\"\\u{10ffff}\"")]
        [InlineData(0x1F600, "'\U0001F600'", "\"\U0001F600\"")]
        [InlineData(0x27, "'\\''", "\"'\"")]
        [InlineData(0x22, "'\"'", "\"\\\"\"")]
        [InlineData(0x61, "'a'", "\"a\"")]
        [InlineData(0x85, "'\\u{85}'", "\"\\u{85}\"")]
        [InlineData(0x3000, "'\\u{3000}'", "\"\\u{3000}\"")]
        [InlineData(0x0, "'\\0'", "\"\\0\"")]
        public void CodePointsFormatLikeRustDebug(int codePoint, string asChar, string asString)
        {
            var rune = new Rune(codePoint);
            Assert.Equal(asChar, RustStrDebug.Format(rune));
            Assert.Equal(asString, RustStrDebug.Format(rune.ToString()));
            if (rune.IsBmp)
                Assert.Equal(asChar, RustStrDebug.Format((char)codePoint));
        }

        [Fact]
        public void QuotesAreEscapedOnlyWhenTheyDelimit()
        {
            Assert.Equal("\"a'b\\\"c\"", RustStrDebug.Format("a'b\"c"));
        }

        /// <summary>
        /// A Rust string cannot hold a lone surrogate; a .NET string can, and it is written like any other unprintable
        /// code point. A well-formed pair is one code point.
        /// </summary>
        [Fact]
        public void LoneSurrogatesAreEscaped()
        {
            Assert.Equal("\"a\\u{d800}b\"", RustStrDebug.Format("a\ud800b"));
            Assert.Equal("\"\\u{dc00}\"", RustStrDebug.Format("\udc00"));
            Assert.Equal("\"\\u{dc00}\\u{d800}\"", RustStrDebug.Format("\udc00\ud800"));
            Assert.Equal("\"x\\u{d83d}\"", RustStrDebug.Format("x\ud83d"));
            Assert.Equal("\"\U0001F600\"", RustStrDebug.Format("\ud83d\ude00"));
            Assert.Equal("'\\u{d800}'", RustStrDebug.Format('\ud800'));
        }

    }

}
