using System.Globalization;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Checks the rustc-generated <see cref="RustDebugEscapes"/> table against .NET's Unicode categories, an independent
    /// reference, wherever the two can agree: Rust escapes Cc, Cf, Cs, Co, Zl, Zp, Zs other than the space, and every
    /// grapheme extender (all Mn and Me), and prints the other assigned categories raw.
    /// </summary>
    public class RustDebugEscapesTests
    {

        [Fact]
        public void AsciiEscapesExactlyTheControls()
        {
            for (var c = 0; c < 0x80; c++)
                Assert.Equal(c < 0x20 || c == 0x7F, RustDebugEscapes.IsEscaped(c));
        }

        [Fact]
        public void TheTableAgreesWithUnicodeCategoriesEverywhereTheyAreComparable()
        {
            for (var cp = 0; cp <= 0x10FFFF; cp++)
            {
                bool expected;
                switch (CharUnicodeInfo.GetUnicodeCategory(cp))
                {
                    case UnicodeCategory.Control:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.Surrogate:
                    case UnicodeCategory.PrivateUse:
                    case UnicodeCategory.LineSeparator:
                    case UnicodeCategory.ParagraphSeparator:
                    case UnicodeCategory.NonSpacingMark:
                    case UnicodeCategory.EnclosingMark:
                        expected = true;
                        break;
                    case UnicodeCategory.SpaceSeparator:
                        expected = cp != 0x20;
                        break;

                    // Unassigned code points depend on the Unicode version (Rust 1.99 has 17.0, .NET an older one),
                    // and only some spacing marks extend graphemes.
                    case UnicodeCategory.OtherNotAssigned:
                    case UnicodeCategory.SpacingCombiningMark:
                        continue;

                    default:
                        expected = false;
                        break;
                }

                // Rust also escapes the Hangul fillers, which render as nothing. U+1171E became a spacing mark in
                // Unicode 16; .NET 8's tables still call it nonspacing.
                if (cp is 0x115F or 0x1160 or 0x3164 or 0xFFA0)
                    expected = true;
                else if (cp == 0x1171E)
                    continue;

                Assert.True(expected == RustDebugEscapes.IsEscaped(cp), cp.ToString("X", CultureInfo.InvariantCulture));
            }
        }

        [Fact]
        public void TheEndsOfTheRangeAreHandled()
        {
            Assert.False(RustDebugEscapes.IsEscaped(0xA1));
            Assert.True(RustDebugEscapes.IsEscaped(0xA0));
            Assert.True(RustDebugEscapes.IsEscaped(0x3347A));
            Assert.True(RustDebugEscapes.IsEscaped(0x10FFFF));
            Assert.False(RustDebugEscapes.IsEscaped(0x33479));
        }

    }

}
