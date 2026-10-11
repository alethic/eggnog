using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Ports of egglog's base value tests (core-relations/src/base_values/tests.rs) and of the doc tests in
    /// core-relations/src/base_values/unboxed.rs. The random samples come from a seeded <see cref="Random"/> in place of
    /// Rust's <c>StdRng::seed_from_u64(42)</c>, so they differ from egglog's but are just as reproducible.
    /// </summary>
    public class BaseValuesTests
    {

        [Fact]
        public void BasePrinting()
        {
            var bases = new BaseValues();
            bases.RegisterType<Int64Codec, long>();
            var ty = bases.GetTypeId<Int64Codec, long>();
            var val = bases.Get<Int64Codec, long>(24L);
            Assert.Equal("24", new BaseValuePrinter(bases, ty, val).ToString());
        }

        [Fact]
        public void RoundtripSmallIntegers()
        {
            var bases = new BaseValues();
            var rng = new Random(42);

            // Test u8
            bases.RegisterType<ByteCodec, byte>();
            foreach (var val in new byte[] { 0, 1, 127, 255 })
                Assert.Equal(val, bases.Unwrap<ByteCodec, byte>(bases.Get<ByteCodec, byte>(val)));

            // Random u8 samples
            for (var i = 0; i < 100; i++)
            {
                var val = (byte)rng.Next(256);
                Assert.Equal(val, bases.Unwrap<ByteCodec, byte>(bases.Get<ByteCodec, byte>(val)));
            }

            // Test u16
            bases.RegisterType<UInt16Codec, ushort>();
            foreach (var val in new ushort[] { 0, 1, 255, 256, 65535 })
                Assert.Equal(val, bases.Unwrap<UInt16Codec, ushort>(bases.Get<UInt16Codec, ushort>(val)));

            // Random u16 samples
            for (var i = 0; i < 100; i++)
            {
                var val = (ushort)rng.Next(65536);
                Assert.Equal(val, bases.Unwrap<UInt16Codec, ushort>(bases.Get<UInt16Codec, ushort>(val)));
            }

            // Test u32
            bases.RegisterType<UInt32Codec, uint>();
            foreach (var val in new uint[] { 0, 1, 255, 65536, 2147483647, 4294967295 })
                Assert.Equal(val, bases.Unwrap<UInt32Codec, uint>(bases.Get<UInt32Codec, uint>(val)));

            // Random u32 samples
            for (var i = 0; i < 100; i++)
            {
                var val = (uint)rng.NextInt64(0, 1L << 32);
                Assert.Equal(val, bases.Unwrap<UInt32Codec, uint>(bases.Get<UInt32Codec, uint>(val)));
            }

            // Test i8
            bases.RegisterType<SByteCodec, sbyte>();
            foreach (var val in new sbyte[] { -128, -1, 0, 1, 127 })
                Assert.Equal(val, bases.Unwrap<SByteCodec, sbyte>(bases.Get<SByteCodec, sbyte>(val)));

            // Random i8 samples
            for (var i = 0; i < 100; i++)
            {
                var val = (sbyte)rng.Next(-128, 128);
                Assert.Equal(val, bases.Unwrap<SByteCodec, sbyte>(bases.Get<SByteCodec, sbyte>(val)));
            }

            // Test i16
            bases.RegisterType<Int16Codec, short>();
            foreach (var val in new short[] { -32768, -1, 0, 1, 32767 })
                Assert.Equal(val, bases.Unwrap<Int16Codec, short>(bases.Get<Int16Codec, short>(val)));

            // Random i16 samples
            for (var i = 0; i < 100; i++)
            {
                var val = (short)rng.Next(-32768, 32768);
                Assert.Equal(val, bases.Unwrap<Int16Codec, short>(bases.Get<Int16Codec, short>(val)));
            }

            // Test i32
            bases.RegisterType<Int32Codec, int>();
            foreach (var val in new int[] { -2147483648, -1, 0, 1, 2147483647 })
                Assert.Equal(val, bases.Unwrap<Int32Codec, int>(bases.Get<Int32Codec, int>(val)));

            // Random i32 samples
            for (var i = 0; i < 100; i++)
            {
                var val = (int)rng.NextInt64(int.MinValue, 1L + int.MaxValue);
                Assert.Equal(val, bases.Unwrap<Int32Codec, int>(bases.Get<Int32Codec, int>(val)));
            }
        }

        [Fact]
        public void RoundtripBool()
        {
            var bases = new BaseValues();
            bases.RegisterType<BooleanCodec, bool>();

            foreach (var val in new[] { true, false })
                Assert.Equal(val, bases.Unwrap<BooleanCodec, bool>(bases.Get<BooleanCodec, bool>(val)));

            // Random bool samples
            var rng = new Random(42);
            for (var i = 0; i < 100; i++)
            {
                var val = rng.Next(2) == 1;
                Assert.Equal(val, bases.Unwrap<BooleanCodec, bool>(bases.Get<BooleanCodec, bool>(val)));
            }
        }

        [Fact]
        public void RoundtripUnit()
        {
            var bases = new BaseValues();
            bases.RegisterType<UnitCodec, ValueTuple>();

            var val = default(ValueTuple);
            var boxed = bases.Get<UnitCodec, ValueTuple>(val);
            Assert.Equal(val, bases.Unwrap<UnitCodec, ValueTuple>(boxed));
        }

        [Fact]
        public void RoundtripMediumIntegersUnboxable()
        {
            var bases = new BaseValues();
            var rng = new Random(42);

            // Test u64 values that fit in 31 bits (unboxable)
            bases.RegisterType<UInt64Codec, ulong>();
            foreach (var val in new ulong[] { 0, 1, 1000, 2147483647 })
                Assert.Equal(val, bases.Unwrap<UInt64Codec, ulong>(bases.Get<UInt64Codec, ulong>(val)));

            for (var i = 0; i < 100; i++)
            {
                var val = (ulong)rng.NextInt64(0, 2147483648L);
                Assert.Equal(val, bases.Unwrap<UInt64Codec, ulong>(bases.Get<UInt64Codec, ulong>(val)));
            }

            bases.RegisterType<Int64Codec, long>();
            foreach (var val in new long[] { 0, 1, 1000, 2147483647 })
                Assert.Equal(val, bases.Unwrap<Int64Codec, long>(bases.Get<Int64Codec, long>(val)));

            for (var i = 0; i < 100; i++)
            {
                var val = rng.NextInt64(0, 2147483648L);
                Assert.Equal(val, bases.Unwrap<Int64Codec, long>(bases.Get<Int64Codec, long>(val)));
            }

            bases.RegisterType<UIntPtrCodec, nuint>();
            foreach (var val in new nuint[] { 0, 1, 1000, 2147483647 })
                Assert.Equal(val, bases.Unwrap<UIntPtrCodec, nuint>(bases.Get<UIntPtrCodec, nuint>(val)));

            for (var i = 0; i < 100; i++)
            {
                var val = (nuint)rng.NextInt64(0, 2147483648L);
                Assert.Equal(val, bases.Unwrap<UIntPtrCodec, nuint>(bases.Get<UIntPtrCodec, nuint>(val)));
            }

            bases.RegisterType<IntPtrCodec, nint>();
            foreach (var val in new nint[] { 0, 1, 1000, 2147483647 })
                Assert.Equal(val, bases.Unwrap<IntPtrCodec, nint>(bases.Get<IntPtrCodec, nint>(val)));

            // Random isize samples (31-bit range)
            for (var i = 0; i < 100; i++)
            {
                var val = (nint)rng.NextInt64(0, 2147483648L);
                Assert.Equal(val, bases.Unwrap<IntPtrCodec, nint>(bases.Get<IntPtrCodec, nint>(val)));
            }
        }

        [Fact]
        public void RoundtripMediumIntegersInterned()
        {
            var bases = new BaseValues();
            var rng = new Random(42);

            // Test u64 values that don't fit in 31 bits (need interning)
            bases.RegisterType<UInt64Codec, ulong>();
            foreach (var val in new ulong[] { 2147483648, 4294967296, ulong.MaxValue })
                Assert.Equal(val, bases.Unwrap<UInt64Codec, ulong>(bases.Get<UInt64Codec, ulong>(val)));

            // Random u64 samples (large values)
            for (var i = 0; i < 100; i++)
            {
                var val = RandomUInt64(rng, 2147483648UL);
                Assert.Equal(val, bases.Unwrap<UInt64Codec, ulong>(bases.Get<UInt64Codec, ulong>(val)));
            }

            // Test i64 values that don't fit in 31 bits (need interning)
            bases.RegisterType<Int64Codec, long>();
            foreach (var val in new long[] { -2147483649L, long.MinValue, long.MaxValue })
                Assert.Equal(val, bases.Unwrap<Int64Codec, long>(bases.Get<Int64Codec, long>(val)));

            // Random i64 samples (values outside 31-bit range)
            for (var i = 0; i < 50; i++)
            {
                var val = rng.NextInt64(long.MinValue, 0);
                Assert.Equal(val, bases.Unwrap<Int64Codec, long>(bases.Get<Int64Codec, long>(val)));
            }

            for (var i = 0; i < 50; i++)
            {
                var val = rng.NextInt64(2147483648L, long.MaxValue);
                Assert.Equal(val, bases.Unwrap<Int64Codec, long>(bases.Get<Int64Codec, long>(val)));
            }

            // Test large usize values (need interning on 64-bit systems)
            bases.RegisterType<UIntPtrCodec, nuint>();
            if (IntPtr.Size == 8)
            {
                foreach (var val in new nuint[] { 2147483648, nuint.MaxValue })
                    Assert.Equal(val, bases.Unwrap<UIntPtrCodec, nuint>(bases.Get<UIntPtrCodec, nuint>(val)));

                // Random usize samples (large values)
                for (var i = 0; i < 100; i++)
                {
                    var val = (nuint)RandomUInt64(rng, 2147483648UL);
                    Assert.Equal(val, bases.Unwrap<UIntPtrCodec, nuint>(bases.Get<UIntPtrCodec, nuint>(val)));
                }
            }

            // Test large isize values (need interning on 64-bit systems)
            bases.RegisterType<IntPtrCodec, nint>();
            if (IntPtr.Size == 8)
            {
                foreach (var val in new nint[] { unchecked((nint)(-2147483649L)), nint.MinValue, nint.MaxValue })
                    Assert.Equal(val, bases.Unwrap<IntPtrCodec, nint>(bases.Get<IntPtrCodec, nint>(val)));

                // Random isize samples (values outside 31-bit range)
                for (var i = 0; i < 50; i++)
                {
                    var val = (nint)rng.NextInt64(long.MinValue, 0);
                    Assert.Equal(val, bases.Unwrap<IntPtrCodec, nint>(bases.Get<IntPtrCodec, nint>(val)));
                }

                for (var i = 0; i < 50; i++)
                {
                    var val = (nint)rng.NextInt64(2147483648L, long.MaxValue);
                    Assert.Equal(val, bases.Unwrap<IntPtrCodec, nint>(bases.Get<IntPtrCodec, nint>(val)));
                }
            }
        }

        /// <summary>
        /// The doc test on <c>bool::try_box</c> (core-relations/src/base_values/unboxed.rs:40-47).
        /// </summary>
        [Fact]
        public void BoolBoxesTrueAsOneAndFalseAsZero()
        {
            Assert.True(BooleanCodec.TryBox(true, out var trueValue));
            Assert.True(BooleanCodec.TryBox(false, out var falseValue));
            Assert.True(BooleanCodec.TryUnbox(trueValue, out var t) && t);
            Assert.True(BooleanCodec.TryUnbox(falseValue, out var f) && f == false);
            Assert.Equal(1u, trueValue.Rep);
            Assert.Equal(0u, falseValue.Rep);
        }

        /// <summary>
        /// The doc test on <c>()::try_box</c> (core-relations/src/base_values/unboxed.rs:57-63).
        /// </summary>
        [Fact]
        public void UnitBoxesAsZero()
        {
            Assert.True(UnitCodec.TryBox(default, out var unitValue));
            Assert.True(UnitCodec.TryUnbox(unitValue, out var unit));
            Assert.Equal(default(ValueTuple), unit);
            Assert.Equal(0u, unitValue.Rep);
        }

        /// <summary>
        /// A uniform <see cref="ulong"/> in <c>[min, ulong.MaxValue]</c>, Rust's <c>random_range(min..=u64::MAX)</c>.
        /// </summary>
        static ulong RandomUInt64(Random rng, ulong min)
        {
            Span<byte> bytes = stackalloc byte[8];
            while (true)
            {
                rng.NextBytes(bytes);
                var x = BitConverter.ToUInt64(bytes);
                if (x >= min)
                    return x;
            }
        }

    }

}
