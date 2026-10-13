using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Ports of egglog's base value tests (core-relations/src/base_values/tests.rs) and of the doc tests in
    /// core-relations/src/base_values/unboxed.rs, and tests of <c>TryGetExisting</c>, the lookup that finds an interned
    /// value without interning it. The random samples come from a seeded <see cref="Random"/> in place of Rust's
    /// <c>StdRng::seed_from_u64(42)</c>, so they differ from egglog's but are just as reproducible.
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
        /// The doc test on <c>bool::try_box</c> (core-relations/src/base_values/unboxed.rs:42-48).
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
        /// The doc test on <c>()::try_box</c> (core-relations/src/base_values/unboxed.rs:58-62).
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
        /// <c>TryGetExisting</c> does not find a value that was never interned, and looking it up interns nothing, for
        /// codecs that intern everything and for one that boxes some values inline.
        /// </summary>
        [Fact]
        public void TryGetExistingFailsForAnUnseenValueAndLeavesTheCountUnchanged()
        {
            var bases = new BaseValues();
            bases.RegisterType<StringCodec, string>();
            bases.RegisterType<Int64Codec, long>();
            bases.RegisterType<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>();
            Assert.Equal(0L, bases.InternedCount);

            Assert.False(bases.TryGetExisting<StringCodec, string>("never interned", out var text));
            Assert.Equal(default, text);
            Assert.False(bases.TryGetExisting<Int64Codec, long>(long.MinValue, out var number));
            Assert.Equal(default, number);
            Assert.False(bases.TryGetExisting<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new BaseValueTestPoint(1, 2), out var point));
            Assert.Equal(default, point);
            Assert.Equal(0L, bases.InternedCount);
            Assert.Equal(0, bases.GetTable<StringCodec, string>().Table.Count);
            Assert.Equal(0, bases.GetTable<Int64Codec, long>().Table.Count);

            // Interning other values of the same types does not make the unseen ones visible, nor do failed lookups add
            // them.
            bases.Get<StringCodec, string>("seen");
            bases.Get<Int64Codec, long>(-1L);
            Assert.Equal(2L, bases.InternedCount);
            Assert.False(bases.TryGetExisting<StringCodec, string>("never interned", out _));
            Assert.False(bases.TryGetExisting<Int64Codec, long>(long.MinValue, out _));
            Assert.False(bases.TryGetExisting<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new BaseValueTestPoint(1, 2), out _));
            Assert.Equal(2L, bases.InternedCount);
            Assert.Equal(1, bases.GetTable<StringCodec, string>().Table.Count);
            Assert.Equal(1, bases.GetTable<Int64Codec, long>().Table.Count);
        }

        /// <summary>
        /// <c>TryGetExisting</c> finds a value that was interned with the very <see cref="Value"/> <c>Get</c> returned
        /// (tagged or not, as its codec stores it), it unwraps to the value, and the lookup interns nothing.
        /// </summary>
        [Fact]
        public void TryGetExistingRoundTripsASeenValue()
        {
            var bases = new BaseValues();
            bases.RegisterType<StringCodec, string>();
            bases.RegisterType<Int64Codec, long>();
            bases.RegisterType<DoubleCodec, double>();
            bases.RegisterType<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>();

            var kind = bases.Get<StringCodec, string>("kind");
            var smallest = bases.Get<Int64Codec, long>(long.MinValue);
            var belowInline = bases.Get<Int64Codec, long>(-2147483649L);
            var half = bases.Get<DoubleCodec, double>(1.5);
            var point = bases.Get<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new BaseValueTestPoint(3, 4));
            Assert.Equal(5L, bases.InternedCount);

            // An equal string that is a different instance.
            Assert.True(bases.TryGetExisting<StringCodec, string>(new string(new[] { 'k', 'i', 'n', 'd' }), out var foundKind));
            Assert.Equal(kind, foundKind);
            Assert.Equal("kind", bases.Unwrap<StringCodec, string>(foundKind));

            // Interned 64-bit integers carry the interned tag.
            Assert.True(bases.TryGetExisting<Int64Codec, long>(long.MinValue, out var foundSmallest));
            Assert.Equal(smallest, foundSmallest);
            Assert.True(foundSmallest.Rep >= BaseValues.InternedTag);
            Assert.Equal(long.MinValue, bases.Unwrap<Int64Codec, long>(foundSmallest));
            Assert.True(bases.TryGetExisting<Int64Codec, long>(-2147483649L, out var foundBelowInline));
            Assert.Equal(belowInline, foundBelowInline);
            Assert.Equal(-2147483649L, bases.Unwrap<Int64Codec, long>(foundBelowInline));

            Assert.True(bases.TryGetExisting<DoubleCodec, double>(1.5, out var foundHalf));
            Assert.Equal(half, foundHalf);
            Assert.Equal(1.5, bases.Unwrap<DoubleCodec, double>(foundHalf));

            Assert.True(bases.TryGetExisting<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new BaseValueTestPoint(3, 4), out var foundPoint));
            Assert.Equal(point, foundPoint);
            Assert.Equal(new BaseValueTestPoint(3, 4), bases.Unwrap<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(foundPoint));

            Assert.Equal(5L, bases.InternedCount);
        }

        /// <summary>
        /// <c>TryGetExisting</c> always finds a value that boxes inline, with the <see cref="Value"/> <c>Get</c> gives
        /// it, without consulting the registry (none of these types is registered, so touching a table would throw) and
        /// without adding to any intern table.
        /// </summary>
        [Fact]
        public void TryGetExistingOfAnInlineValueNeverTouchesATable()
        {
            var bases = new BaseValues();

            Assert.True(bases.TryGetExisting<Int64Codec, long>(42L, out var answer));
            Assert.Equal(new Value(42), answer);
            Assert.True(bases.TryGetExisting<Int64Codec, long>(2147483647L, out var largestInline));
            Assert.Equal(new Value(2147483647u), largestInline);
            Assert.True(bases.TryGetExisting<BooleanCodec, bool>(true, out var yes));
            Assert.Equal(new Value(1), yes);
            Assert.True(bases.TryGetExisting<UnitCodec, ValueTuple>(default, out var unit));
            Assert.Equal(new Value(0), unit);
            Assert.True(bases.TryGetExisting<Int32Codec, int>(int.MinValue, out var smallest));
            Assert.Equal(bases.Get<Int32Codec, int>(int.MinValue), smallest);
            Assert.True(bases.TryGetExisting<UInt64Codec, ulong>(7UL, out var seven));
            Assert.Equal(new Value(7), seven);

            // Registered types: inline values still leave their intern tables empty.
            bases.RegisterType<Int64Codec, long>();
            bases.RegisterType<BooleanCodec, bool>();
            Assert.True(bases.TryGetExisting<Int64Codec, long>(0L, out var zero));
            Assert.Equal(bases.Get<Int64Codec, long>(0L), zero);
            Assert.True(bases.TryGetExisting<BooleanCodec, bool>(false, out _));
            Assert.Equal(0, bases.GetTable<Int64Codec, long>().InternedCount);
            Assert.Equal(0, bases.GetTable<BooleanCodec, bool>().InternedCount);
            Assert.Equal(0L, bases.InternedCount);
        }

        /// <summary>
        /// <c>TryGetExisting</c> of a value that would have to be interned, of a type that is not registered, throws as
        /// <c>Get</c> does: no registry can have interned it, and asking is a misuse.
        /// </summary>
        [Fact]
        public void TryGetExistingOfAnUnregisteredInternedTypeThrows()
        {
            var bases = new BaseValues();
            Assert.Throws<InvalidOperationException>(() => bases.TryGetExisting<StringCodec, string>("x", out _));
            Assert.Throws<InvalidOperationException>(() => bases.TryGetExisting<Int64Codec, long>(-1L, out _));
        }

        /// <summary>
        /// <see cref="BaseValues.InternedCount"/> sums every intern table, counts each distinct value once, ignores
        /// inline values, and sees values interned through a clone in the tables the clone shares.
        /// </summary>
        [Fact]
        public void InternedCountSumsEveryInternTable()
        {
            var bases = new BaseValues();
            bases.RegisterType<StringCodec, string>();
            bases.RegisterType<Int64Codec, long>();
            bases.RegisterType<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>();

            bases.Get<StringCodec, string>("a");
            bases.Get<StringCodec, string>("b");
            bases.Get<StringCodec, string>("a");
            bases.Get<Int64Codec, long>(-1L);
            bases.Get<Int64Codec, long>(-2L);
            bases.Get<Int64Codec, long>(7L);
            bases.Get<InternedCodec<BaseValueTestPoint>, BaseValueTestPoint>(new BaseValueTestPoint(0, 0));
            Assert.Equal(5L, bases.InternedCount);

            var clone = bases.Clone();
            Assert.Equal(5L, clone.InternedCount);
            clone.Get<StringCodec, string>("c");
            Assert.Equal(6L, bases.InternedCount);
            Assert.Equal(6L, clone.InternedCount);

            // A type registered after the clone was made belongs to one registry only.
            clone.RegisterType<DoubleCodec, double>();
            clone.Get<DoubleCodec, double>(2.5);
            Assert.Equal(6L, bases.InternedCount);
            Assert.Equal(7L, clone.InternedCount);
        }

        /// <summary>
        /// <c>TryGetExisting</c> lookups that race with interning never intern and never see a wrong value; once
        /// interning ends, every value is found with the <see cref="Value"/> <c>Get</c> gave it.
        /// </summary>
        [Fact]
        public async Task TryGetExistingAgreesWithConcurrentInterning()
        {
            const int keys = 20_000;
            const int readers = 4;
            var bases = new BaseValues();
            bases.RegisterType<StringCodec, string>();
            var boxed = new Value[keys];

            using var writing = new CancellationTokenSource();
            var lookups = Enumerable.Range(0, readers).Select(r => Task.Factory.StartNew(() =>
            {
                var random = new Random(r);
                while (writing.IsCancellationRequested == false)
                {
                    var k = random.Next(keys);
                    if (bases.TryGetExisting<StringCodec, string>(Key(k), out var value))
                        Assert.Equal(Key(k), bases.Unwrap<StringCodec, string>(value));
                }
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            for (var k = 0; k < keys; k++)
                boxed[k] = bases.Get<StringCodec, string>(Key(k));

            writing.Cancel();
            await Task.WhenAll(lookups);

            Assert.Equal((long)keys, bases.InternedCount);
            for (var k = 0; k < keys; k++)
            {
                Assert.True(bases.TryGetExisting<StringCodec, string>(Key(k), out var value));
                Assert.Equal(boxed[k], value);
            }

            Assert.False(bases.TryGetExisting<StringCodec, string>(Key(keys), out _));
            Assert.Equal((long)keys, bases.InternedCount);

            static string Key(int k) => "key" + k.ToString(CultureInfo.InvariantCulture);
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

        [Fact]
        public void SignedZerosAndNaNsResolveAsGetResolvesThem()
        {
            var bases = new BaseValues();
            bases.RegisterType<DoubleCodec, double>();

            Assert.False(bases.TryGetExisting<DoubleCodec, double>(-0.0, out _));
            Assert.False(bases.TryGetExisting<DoubleCodec, double>(0.0, out _));
            Assert.False(bases.TryGetExisting<DoubleCodec, double>(double.NaN, out _));
            Assert.Equal(0L, bases.InternedCount);

            var zero = bases.Get<DoubleCodec, double>(0.0);
            var nan = bases.Get<DoubleCodec, double>(double.NaN);
            Assert.True(bases.TryGetExisting<DoubleCodec, double>(-0.0, out var negativeZero));
            Assert.Equal(zero, negativeZero);
            Assert.Equal(bases.Get<DoubleCodec, double>(-0.0), negativeZero);
            Assert.True(bases.TryGetExisting<DoubleCodec, double>(BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0001UL)), out var otherNaN));
            Assert.Equal(nan, otherNaN);
            Assert.Equal(2L, bases.InternedCount);
        }

        [Fact]
        public void AClonesLookupsSeeTheSharedTables()
        {
            var bases = new BaseValues();
            bases.RegisterType<StringCodec, string>();
            var clone = bases.Clone();

            var a = bases.Get<StringCodec, string>("a");
            Assert.True(clone.TryGetExisting<StringCodec, string>("a", out var found));
            Assert.Equal(a, found);

            var b = clone.Get<StringCodec, string>("b");
            Assert.True(bases.TryGetExisting<StringCodec, string>("b", out var foundB));
            Assert.Equal(b, foundB);

            Assert.False(clone.TryGetExisting<StringCodec, string>("c", out _));
            Assert.Equal(2L, bases.InternedCount);
            Assert.Equal(2L, clone.InternedCount);
        }

        [Fact]
        public void WideValuesThatMustBeInternedAreLookedUpWithoutBeingAdded()
        {
            var bases = new BaseValues();
            bases.RegisterType<UInt64Codec, ulong>();
            bases.RegisterType<IntPtrCodec, nint>();
            bases.RegisterType<UIntPtrCodec, nuint>();

            Assert.False(bases.TryGetExisting<UInt64Codec, ulong>(ulong.MaxValue, out _));
            Assert.False(bases.TryGetExisting<IntPtrCodec, nint>(-5, out _));
            Assert.False(bases.TryGetExisting<UIntPtrCodec, nuint>(nuint.MaxValue, out _));
            Assert.Equal(0L, bases.InternedCount);

            var big = bases.Get<UInt64Codec, ulong>(ulong.MaxValue);
            var negative = bases.Get<IntPtrCodec, nint>(-5);
            Assert.True(bases.TryGetExisting<UInt64Codec, ulong>(ulong.MaxValue, out var foundBig));
            Assert.Equal(big, foundBig);
            Assert.True(bases.TryGetExisting<IntPtrCodec, nint>(-5, out var foundNegative));
            Assert.Equal(negative, foundNegative);
            Assert.True(foundBig.Rep >= BaseValues.InternedTag);
            Assert.Equal(2L, bases.InternedCount);

            Assert.True(bases.TryGetExisting<UInt64Codec, ulong>(5UL, out var inline));
            Assert.Equal(bases.Get<UInt64Codec, ulong>(5UL), inline);
            Assert.Equal(2L, bases.InternedCount);
        }

        [Fact]
        public void TryGetExistingRejectsANullKeyAsGetDoes()
        {
            var bases = new BaseValues();
            bases.RegisterType<StringCodec, string>();
            Assert.Throws<ArgumentNullException>(() => bases.Get<StringCodec, string>(null!));
            Assert.Throws<ArgumentNullException>(() => bases.TryGetExisting<StringCodec, string>(null!, out _));
            Assert.Equal(0L, bases.InternedCount);
        }

    }

}
