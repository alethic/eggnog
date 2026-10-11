using System;
using System.Numerics;
using System.Text;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="InternedCodec{T}"/> formats as egglog's <c>Boxed&lt;T&gt;</c>, whose <c>Debug</c> forwards to the
    /// wrapped value's (mod.rs:209-213). Every expected string was printed by rustc 1.99 for the Rust counterpart type.
    /// </summary>
    public class InternedCodecTests
    {

        [Fact]
        public void PrimitivesFormatLikeTheirRustCounterparts()
        {
            Assert.Equal("\"a'b\\\"c\"", InternedCodec<string>.Format("a'b\"c"));
            Assert.Equal("1.0", InternedCodec<double>.Format(1.0));
            Assert.Equal("1658206780088562.3", InternedCodec<double>.Format(BitConverter.UInt64BitsToDouble(0x43179085685d83c9UL)));
            Assert.Equal("1.0", InternedCodec<float>.Format(1.0f));
            Assert.Equal("-0.0", InternedCodec<float>.Format(-0.0f));
            Assert.Equal("false", InternedCodec<bool>.Format(false));
            Assert.Equal("'x'", InternedCodec<char>.Format('x'));
            Assert.Equal("'\\''", InternedCodec<char>.Format('\''));
            Assert.Equal("'\\u{e0100}'", InternedCodec<Rune>.Format(new Rune(0xE0100)));
            Assert.Equal("()", InternedCodec<ValueTuple>.Format(default));
            Assert.Equal("42", InternedCodec<int>.Format(42));
            Assert.Equal("1267650600228229401496703205376", InternedCodec<UInt128>.Format(UInt128.One << 100));
            Assert.Equal("-1267650600228229401496703205376", InternedCodec<BigInteger>.Format(-(BigInteger.One << 100)));
        }

        /// <summary>
        /// Through the registry, as egglog's printer formats a <c>Boxed&lt;String&gt;</c> column.
        /// </summary>
        [Fact]
        public void TheFrontendStringSortPrintsQuoted()
        {
            var bases = new BaseValues();
            var s = bases.RegisterType<InternedCodec<string>, string>();
            var value = bases.Get<InternedCodec<string>, string>("abc");
            Assert.Equal("\"abc\"", new BaseValuePrinter(bases, s, value).ToString());
        }

    }

}
