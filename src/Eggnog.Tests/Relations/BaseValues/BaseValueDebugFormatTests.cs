using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests: pin the codecs' <c>Format</c> to Rust's <c>Debug</c> output, which egglog's
    /// <c>BaseValuePrinter</c> (core-relations/src/base_values/mod.rs:56-64) produces. Every expected string below was
    /// printed by rustc 1.99 (<c>format!("{:?}", x)</c>), not derived by hand.
    /// </summary>
    public class BaseValueDebugFormatTests
    {

        /// <summary>
        /// Doubles whose exact value lies exactly halfway between two shortest 17-digit candidates. Rust's shortest
        /// formatting rounds such ties up in magnitude; .NET's "R" format rounds them to even, so the last digit differs.
        /// </summary>
        [Theory]
        [InlineData(0x43179085685d83c9UL, "1658206780088562.3")]
        [InlineData(0xc30deb9ee957fc2aUL, "-1052730259603333.3")]
        [InlineData(0x42ea8090bb0f6d84UL, "233115890514796.13")]
        [InlineData(0x431f19b7745d2001UL, "2188500000000000.3")]
        [InlineData(0x431cb1144734c001UL, "2019000000000000.3")]
        public void DoubleTiesFormatLikeRustDebug(ulong bits, string expected)
        {
            Assert.Equal(expected, DoubleCodec.Format(BitConverter.UInt64BitsToDouble(bits)));
        }

        /// <summary>
        /// Through the registry, as egglog's printer formats a column value.
        /// </summary>
        [Fact]
        public void DoubleTiesPrintLikeRustDebugThroughThePrinter()
        {
            var bases = new BaseValues();
            var f64 = bases.RegisterType<DoubleCodec, double>();
            var value = bases.Get<DoubleCodec, double>(BitConverter.UInt64BitsToDouble(0x43179085685d83c9UL));
            Assert.Equal("1658206780088562.3", new BaseValuePrinter(bases, f64, value).ToString());
        }

        /// <summary>
        /// Rust's <c>Debug</c> for <c>str</c> escapes every character that is not printable by its Unicode tables
        /// (format characters, non-ASCII spaces, line and paragraph separators, private-use and unassigned code points)
        /// and every grapheme-extending character, not only the C0/C1 controls.
        /// </summary>
        [Theory]
        [InlineData("\u00ad", "\"\\u{ad}\"")]
        [InlineData("e\u0301", "\"e\\u{301}\"")]
        [InlineData("\u0301", "\"\\u{301}\"")]
        [InlineData("\u200b", "\"\\u{200b}\"")]
        [InlineData("\u200d", "\"\\u{200d}\"")]
        [InlineData("\u00a0", "\"\\u{a0}\"")]
        [InlineData("\u2028", "\"\\u{2028}\"")]
        [InlineData("\u3000", "\"\\u{3000}\"")]
        [InlineData("\ufeff", "\"\\u{feff}\"")]
        [InlineData("\u0378", "\"\\u{378}\"")]
        [InlineData("\ue000", "\"\\u{e000}\"")]
        [InlineData("\U000E0001", "\"\\u{e0001}\"")]
        public void StringsEscapeLikeRustDebug(string value, string expected)
        {
            Assert.Equal(expected, StringCodec.Format(value));
        }

        /// <summary>
        /// Characters Rust prints raw stay raw (a regression guard for any fix of the escaping above).
        /// </summary>
        [Theory]
        [InlineData("\U0001F600", "\"\U0001F600\"")]
        [InlineData("\ufffd", "\"\ufffd\"")]
        [InlineData("it's", "\"it's\"")]
        [InlineData(" ", "\" \"")]
        public void PrintableStringsStayRaw(string value, string expected)
        {
            Assert.Equal(expected, StringCodec.Format(value));
        }

        /// <summary>
        /// <see cref="InternedCodec{T}"/> is the port of egglog's <c>Boxed&lt;T&gt;</c>, whose <c>Debug</c> forwards to
        /// the wrapped value's <c>Debug</c> (mod.rs:211-215): <c>Boxed&lt;String&gt;</c> (the frontend's <c>S</c>) prints
        /// quoted, <c>Boxed&lt;bool&gt;</c> lower-case and <c>Boxed&lt;char&gt;</c> in single quotes.
        /// </summary>
        [Fact]
        public void InternedCodecFormatsLikeBoxedDebug()
        {
            Assert.Equal("\"abc\"", InternedCodec<string>.Format("abc"));
            Assert.Equal("true", InternedCodec<bool>.Format(true));
            Assert.Equal("'a'", InternedCodec<char>.Format('a'));
        }

    }

}
