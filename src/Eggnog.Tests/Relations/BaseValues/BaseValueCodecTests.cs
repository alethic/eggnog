using System;
using System.Numerics;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Pins each codec to egglog's exact encoding (core-relations/src/base_values/unboxed.rs and mod.rs:158-188) and
    /// to Rust's <c>Debug</c> formatting.
    /// </summary>
    public class BaseValueCodecTests
    {

        [Fact]
        public void SmallIntegersBoxAsTheirBitsExhaustively()
        {
            var bases = new BaseValues();
            for (var i = 0; i <= byte.MaxValue; i++)
            {
                var b = (byte)i;
                Assert.Equal((uint)b, bases.Get<ByteCodec, byte>(b).Rep);
                Assert.Equal(b, bases.Unwrap<ByteCodec, byte>(new Value(b)));

                var s = (sbyte)i;
                Assert.Equal(unchecked((uint)(int)s), bases.Get<SByteCodec, sbyte>(s).Rep);
                Assert.Equal(s, bases.Unwrap<SByteCodec, sbyte>(bases.Get<SByteCodec, sbyte>(s)));
            }

            for (var i = 0; i <= ushort.MaxValue; i++)
            {
                var u = (ushort)i;
                Assert.Equal((uint)u, bases.Get<UInt16Codec, ushort>(u).Rep);
                Assert.Equal(u, bases.Unwrap<UInt16Codec, ushort>(new Value(u)));

                var s = (short)i;
                Assert.Equal(unchecked((uint)(int)s), bases.Get<Int16Codec, short>(s).Rep);
                Assert.Equal(s, bases.Unwrap<Int16Codec, short>(bases.Get<Int16Codec, short>(s)));
            }

            Assert.Equal(0xFFFF_FF80u, bases.Get<SByteCodec, sbyte>(sbyte.MinValue).Rep);
            Assert.Equal(0xFFFF_8000u, bases.Get<Int16Codec, short>(short.MinValue).Rep);
            Assert.Equal(0x8000_0000u, bases.Get<Int32Codec, int>(int.MinValue).Rep);
            Assert.Equal(0x7FFF_FFFFu, bases.Get<Int32Codec, int>(int.MaxValue).Rep);
            Assert.Equal(uint.MaxValue, bases.Get<UInt32Codec, uint>(uint.MaxValue).Rep);
        }

        [Fact]
        public void SmallIntegersUnwrapByTruncation()
        {
            // Rust's `val.rep() as u8` and friends keep the low bits of any value.
            var bases = new BaseValues();
            Assert.Equal((byte)0xFF, bases.Unwrap<ByteCodec, byte>(new Value(0x1234_56FF)));
            Assert.Equal((sbyte)-1, bases.Unwrap<SByteCodec, sbyte>(new Value(0x0000_00FF)));
            Assert.Equal((ushort)0x5678, bases.Unwrap<UInt16Codec, ushort>(new Value(0x1234_5678)));
            Assert.Equal((short)-2, bases.Unwrap<Int16Codec, short>(new Value(0x0000_FFFE)));
            Assert.Equal(-1, bases.Unwrap<Int32Codec, int>(new Value(uint.MaxValue)));
            Assert.Equal(0xDEAD_BEEFu, bases.Unwrap<UInt32Codec, uint>(new Value(0xDEAD_BEEF)));
        }

        [Fact]
        public void SignedMinusOneBoxesToTheStaleRepresentation()
        {
            // Kept from egglog (unboxed.rs:13-31): such values must stay out of a table's first column.
            var bases = new BaseValues();
            Assert.Equal(Value.StaleRep, bases.Get<SByteCodec, sbyte>(-1).Rep);
            Assert.Equal(Value.StaleRep, bases.Get<Int16Codec, short>(-1).Rep);
            Assert.Equal(Value.StaleRep, bases.Get<Int32Codec, int>(-1).Rep);
            Assert.Equal(Value.StaleRep, bases.Get<UInt32Codec, uint>(uint.MaxValue).Rep);
        }

        [Fact]
        public void BoolAndUnitBoxInline()
        {
            var bases = new BaseValues();
            Assert.Equal(1u, bases.Get<BooleanCodec, bool>(true).Rep);
            Assert.Equal(0u, bases.Get<BooleanCodec, bool>(false).Rep);
            Assert.False(bases.Unwrap<BooleanCodec, bool>(new Value(0)));
            Assert.True(bases.Unwrap<BooleanCodec, bool>(new Value(1)));
            Assert.True(bases.Unwrap<BooleanCodec, bool>(new Value(2)));
            Assert.True(bases.Unwrap<BooleanCodec, bool>(Value.Stale));

            Assert.Equal(0u, bases.Get<UnitCodec, ValueTuple>(default).Rep);
            Assert.Equal(default(ValueTuple), bases.Unwrap<UnitCodec, ValueTuple>(new Value(12345)));
        }

        [Fact]
        public void MediumIntegersInlineExactlyBelowTwoToThe31()
        {
            var bases = new BaseValues();
            bases.RegisterType<Int64Codec, long>();
            bases.RegisterType<UInt64Codec, ulong>();
            bases.RegisterType<IntPtrCodec, nint>();
            bases.RegisterType<UIntPtrCodec, nuint>();

            foreach (var x in new long[] { 0, 1, 1000, 0x7FFF_FFFE, 0x7FFF_FFFF })
            {
                Assert.Equal((uint)x, bases.Get<Int64Codec, long>(x).Rep);
                Assert.Equal((uint)x, bases.Get<UInt64Codec, ulong>((ulong)x).Rep);
                Assert.Equal((uint)x, bases.Get<IntPtrCodec, nint>((nint)x).Rep);
                Assert.Equal((uint)x, bases.Get<UIntPtrCodec, nuint>((nuint)x).Rep);
            }

            // The first value that does not fit is interned first, so it gets index 0.
            Assert.Equal(0x8000_0000u, bases.Get<Int64Codec, long>(0x8000_0000L).Rep);
            Assert.Equal(0x8000_0001u, bases.Get<Int64Codec, long>(-1).Rep);
            Assert.Equal(0x8000_0002u, bases.Get<Int64Codec, long>(long.MinValue).Rep);
            Assert.Equal(0x8000_0000u, bases.Get<Int64Codec, long>(0x8000_0000L).Rep);
            Assert.Equal(-1L, bases.Unwrap<Int64Codec, long>(new Value(0x8000_0001u)));

            Assert.Equal(0x8000_0000u, bases.Get<UInt64Codec, ulong>(ulong.MaxValue).Rep);
            Assert.Equal(0x8000_0001u, bases.Get<UInt64Codec, ulong>(0x8000_0000UL).Rep);

            Assert.Equal(0x8000_0000u, bases.Get<IntPtrCodec, nint>(-1).Rep);
            Assert.Equal(0x8000_0000u, bases.Get<UIntPtrCodec, nuint>(nuint.MaxValue).Rep);
        }

        [Fact]
        public void InterningOnlyTypesUseTheBareIndexInFirstSeenOrder()
        {
            var bases = new BaseValues();
            bases.RegisterType<StringCodec, string>();
            bases.RegisterType<DoubleCodec, double>();
            bases.RegisterType<InternedCodec<BigInteger>, BigInteger>();

            Assert.Equal(0u, bases.Get<StringCodec, string>("a").Rep);
            Assert.Equal(1u, bases.Get<StringCodec, string>("b").Rep);
            Assert.Equal(0u, bases.Get<StringCodec, string>(new string('a', 1)).Rep);
            Assert.Equal(2u, bases.Get<StringCodec, string>("").Rep);
            Assert.Equal("b", bases.Unwrap<StringCodec, string>(new Value(1)));

            // Floats are never inline, not even small integral ones (egglog's F is Boxed<OrderedFloat<f64>>).
            Assert.Equal(0u, bases.Get<DoubleCodec, double>(0.0).Rep);
            Assert.Equal(1u, bases.Get<DoubleCodec, double>(1.0).Rep);
            Assert.Equal(1.0, bases.Unwrap<DoubleCodec, double>(new Value(1)));

            var big = BigInteger.Pow(10, 40);
            Assert.Equal(0u, bases.Get<InternedCodec<BigInteger>, BigInteger>(big).Rep);
            Assert.Equal(1u, bases.Get<InternedCodec<BigInteger>, BigInteger>(BigInteger.One).Rep);
            Assert.Equal(0u, bases.Get<InternedCodec<BigInteger>, BigInteger>(BigInteger.Parse("10000000000000000000000000000000000000000")).Rep);
            Assert.Equal(big, bases.Unwrap<InternedCodec<BigInteger>, BigInteger>(new Value(0)));
        }

        [Fact]
        public void DoublesFollowOrderedFloatEquality()
        {
            var bases = new BaseValues();
            bases.RegisterType<DoubleCodec, double>();

            var nan = bases.Get<DoubleCodec, double>(double.NaN);
            var otherNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0001UL));
            Assert.Equal(nan, bases.Get<DoubleCodec, double>(otherNaN));
            Assert.Equal(nan, bases.Get<DoubleCodec, double>(-double.NaN));
            Assert.True(double.IsNaN(bases.Unwrap<DoubleCodec, double>(nan)));

            // Both zeros are one value; unwrapping yields whichever was interned first.
            var zero = bases.Get<DoubleCodec, double>(0.0);
            Assert.Equal(zero, bases.Get<DoubleCodec, double>(-0.0));
            Assert.False(double.IsNegative(bases.Unwrap<DoubleCodec, double>(zero)));

            var other = new BaseValues();
            other.RegisterType<DoubleCodec, double>();
            var negativeZero = other.Get<DoubleCodec, double>(-0.0);
            Assert.Equal(negativeZero, other.Get<DoubleCodec, double>(0.0));
            Assert.True(double.IsNegative(other.Unwrap<DoubleCodec, double>(negativeZero)));

            Assert.NotEqual(bases.Get<DoubleCodec, double>(double.PositiveInfinity), bases.Get<DoubleCodec, double>(double.NegativeInfinity));
            Assert.NotEqual(bases.Get<DoubleCodec, double>(double.Epsilon), zero);
        }

        [Fact]
        public void UserTypesRoundTripThroughTheInternedCodec()
        {
            var bases = new BaseValues();
            bases.RegisterType<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>();
            var a = bases.Get<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new BaseValueTestPoint(1, 2));
            var b = bases.Get<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new BaseValueTestPoint(2, 1));
            Assert.Equal(0u, a.Rep);
            Assert.Equal(1u, b.Rep);
            Assert.Equal(a, bases.Get<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new BaseValueTestPoint(1, 2)));
            Assert.Equal(new BaseValueTestPoint(2, 1), bases.Unwrap<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(b));
        }

        [Theory]
        [InlineData(1.0, "1.0")]
        [InlineData(-1.0, "-1.0")]
        [InlineData(1.5, "1.5")]
        [InlineData(0.1, "0.1")]
        [InlineData(0.3, "0.3")]
        [InlineData(123.456, "123.456")]
        [InlineData(100.0, "100.0")]
        [InlineData(0.0001, "0.0001")]
        [InlineData(0.00012, "0.00012")]
        [InlineData(0.00001, "1e-5")]
        [InlineData(1.5e-7, "1.5e-7")]
        [InlineData(-2.5e-10, "-2.5e-10")]
        [InlineData(1e15, "1000000000000000.0")]
        [InlineData(9999999999999998.0, "9999999999999998.0")]
        [InlineData(1e16, "1e16")]
        [InlineData(1.5e16, "1.5e16")]
        [InlineData(123456789012345680.0, "1.2345678901234568e17")]
        [InlineData(1e100, "1e100")]
        [InlineData(5e-324, "5e-324")]
        [InlineData(double.MaxValue, "1.7976931348623157e308")]
        [InlineData(0.0, "0.0")]
        [InlineData(-0.0, "-0.0")]
        [InlineData(double.NaN, "NaN")]
        [InlineData(double.PositiveInfinity, "inf")]
        [InlineData(double.NegativeInfinity, "-inf")]
        public void DoublesFormatLikeRustDebug(double value, string expected)
        {
            Assert.Equal(expected, DoubleCodec.Format(value));
        }

        [Fact]
        public void OtherTypesFormatLikeRustDebug()
        {
            Assert.Equal("\"abc\"", StringCodec.Format("abc"));
            Assert.Equal("\"\"", StringCodec.Format(""));
            Assert.Equal("\"a\\\"b\\\\c\\n\\r\\t\\0\"", StringCodec.Format("a\"b\\c\n\r\t\0"));
            Assert.Equal("\"\\u{1b}[0m\\u{7f}\"", StringCodec.Format("\u001b[0m\u007f"));
            Assert.Equal("\"it's ü\"", StringCodec.Format("it's ü"));

            Assert.Equal("true", BooleanCodec.Format(true));
            Assert.Equal("false", BooleanCodec.Format(false));
            Assert.Equal("()", UnitCodec.Format(default));
            Assert.Equal("-9223372036854775808", Int64Codec.Format(long.MinValue));
            Assert.Equal("18446744073709551615", UInt64Codec.Format(ulong.MaxValue));
            Assert.Equal("-128", SByteCodec.Format(sbyte.MinValue));
            Assert.Equal("-12", IntPtrCodec.Format(-12));
            Assert.Equal("100000000000000000000", InternedCodec<BigInteger>.Format(BigInteger.Pow(10, 20)));
            Assert.Equal("BaseValueTestPoint { X = 1, Y = 2 }", InternedCodec<BaseValueTestPoint>.Format(new BaseValueTestPoint(1, 2)));
        }

    }

}
