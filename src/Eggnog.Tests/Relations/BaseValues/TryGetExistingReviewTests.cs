using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review checks for request E1 of the rule-layer spec, beyond the implementer's tests: misses that race with
    /// interning never intern, signed zeros and NaNs resolve as <c>Get</c> resolves them, clones see each other's values,
    /// and wide unsigned and pointer-sized values that must be interned are looked up without being added.
    /// </summary>
    public class TryGetExistingReviewTests
    {

        [Fact]
        public async Task MissesRacingWithInterningNeverIntern()
        {
            const int keys = 20_000;
            var bases = new BaseValues();
            bases.RegisterType<StringCodec, string>();
            bases.RegisterType<Int64Codec, long>();

            using var writing = new CancellationTokenSource();
            var lookups = Enumerable.Range(0, 4).Select(r => Task.Factory.StartNew(() =>
            {
                var random = new Random(r);
                var misses = 0L;
                while (writing.IsCancellationRequested == false)
                {
                    // Odd keys are never interned by anyone: every lookup must miss and must not add the key.
                    var k = random.Next(keys) | 1;
                    if (bases.TryGetExisting<StringCodec, string>(Key(k), out _))
                        throw new InvalidOperationException($"found never-interned key {k}");
                    if (bases.TryGetExisting<Int64Codec, long>(-1L - k, out _))
                        throw new InvalidOperationException($"found never-interned long {-1L - k}");

                    misses++;
                }

                return misses;
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            var written = 0L;
            for (var k = 0; k < keys; k += 2)
            {
                bases.Get<StringCodec, string>(Key(k));
                bases.Get<Int64Codec, long>(-1L - k);
                written += 2;
            }

            writing.Cancel();
            var misses = await Task.WhenAll(lookups);
            Assert.True(misses.Sum() > 0);
            Assert.Equal(written, bases.InternedCount);
            Assert.Equal(keys / 2, bases.GetTable<StringCodec, string>().InternedCount);
            Assert.Equal(keys / 2, bases.GetTable<Int64Codec, long>().InternedCount);

            static string Key(int k) => "key" + k.ToString(CultureInfo.InvariantCulture);
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
