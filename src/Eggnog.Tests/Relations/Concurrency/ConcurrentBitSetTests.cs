using System;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ConcurrentBitSet"/>: a port of egglog's concurrency/src/tests.rs:209-224, plus randomized
    /// comparisons against a <see cref="bool"/> array and concurrent stress across growth.
    /// </summary>
    public sealed class ConcurrentBitSetTests
    {

        [Fact]
        public void GetSetResizeAndClear()
        {
            var bits = new ConcurrentBitSet(0);
            Assert.False(bits.Get(5));
            Assert.False(bits.Get(130));

            bits.Set(5, true);
            bits.Set(130, true);
            Assert.True(bits.Get(5));
            Assert.True(bits.Get(130));
            Assert.False(bits.Get(6));

            bits.Set(5, false);
            Assert.False(bits.Get(5));
            Assert.True(bits.Get(130));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 64)]
        [InlineData(64, 64)]
        [InlineData(65, 128)]
        [InlineData(100, 128)]
        [InlineData(129, 256)]
        [InlineData(1000, 1024)]
        public void CapacityMatchesEgglogRounding(int requested, long allocated)
        {
            // bitset.rs:17-25 rounds to a multiple of 64, then to a power of two.
            Assert.Equal(allocated, new ConcurrentBitSet(requested).Capacity);
        }

        [Theory]
        [InlineData(0, 5, 64)]
        [InlineData(0, 130, 256)]
        [InlineData(0, 64 * 5, 512)]
        [InlineData(128, 128, 256)]
        [InlineData(128, 1000, 1024)]
        [InlineData(64, 64 * 64, 64 * 128)]
        public void GrowthMatchesEgglogResize(int capacity, int index, long allocated)
        {
            // bitset.rs:57 resizes to (cell + 1).next_power_of_two() cells.
            var bits = new ConcurrentBitSet(capacity);
            bits.Set(index, true);
            Assert.Equal(allocated, bits.Capacity);
            Assert.True(bits.Get(index));
        }

        [Fact]
        public void NegativeIndexesAreRejected()
        {
            var bits = new ConcurrentBitSet(0);
            Assert.Throws<ArgumentOutOfRangeException>(() => bits.Get(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => bits.Set(-1, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrentBitSet(-1));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void MatchesReferenceOnRandomOperations(int seed)
        {
            var random = new Random(seed);
            var range = random.Next(1, 20000);
            var bits = new ConcurrentBitSet(random.Next(0, 300));
            var reference = new bool[range];
            for (var step = 0; step < 20000; step++)
            {
                var index = random.Next(range);
                if (random.Next(3) == 0)
                {
                    Assert.Equal(reference[index], bits.Get(index));
                }
                else
                {
                    var value = random.Next(2) == 0;
                    bits.Set(index, value);
                    reference[index] = value;
                }
            }

            for (var i = 0; i < range; i++)
                Assert.Equal(reference[i], bits.Get(i));

            Assert.False(bits.Get(range + 12345));
        }

        [Fact]
        public void ConcurrentSettersNeverLoseBitsAcrossGrowth()
        {
            // Threads interleave bit by bit, so they share every word, while the set grows from nothing.
            const int Threads = 8;
            const int Bits = 200_000;
            for (var round = 0; round < 3; round++)
            {
                var bits = new ConcurrentBitSet(0);
                using var start = new Barrier(Threads);
                var threads = ConcurrencyTestThread.StartMany(Threads, t =>
                {
                    start.SignalAndWait();
                    for (var i = t; i < Bits; i += Threads)
                        bits.Set(i, true);
                });
                ConcurrencyTestThread.JoinAll(threads);

                for (var i = 0; i < Bits; i++)
                    Assert.True(bits.Get(i), $"bit {i} was lost");

                // Now clear the odd bits concurrently and check the even ones survive.
                using var clear = new Barrier(Threads);
                threads = ConcurrencyTestThread.StartMany(Threads, t =>
                {
                    clear.SignalAndWait();
                    for (var i = 2 * t + 1; i < Bits; i += 2 * Threads)
                        bits.Set(i, false);
                });
                ConcurrencyTestThread.JoinAll(threads);

                for (var i = 0; i < Bits; i++)
                    Assert.Equal(i % 2 == 0, bits.Get(i));
            }
        }

        [Fact]
        public void ReadersSeePublishedBitsWhileTheSetGrows()
        {
            // A writer sets bits in increasing order and publishes a watermark; readers check every bit below it.
            const int Bits = 300_000;
            var bits = new ConcurrentBitSet(0);
            var watermark = 0;
            var writer = ConcurrencyTestThread.Start(() =>
            {
                for (var i = 0; i < Bits; i++)
                {
                    bits.Set(i, true);
                    if ((i & 255) == 255)
                        Volatile.Write(ref watermark, i + 1);
                }

                Volatile.Write(ref watermark, Bits);
            });

            var readers = ConcurrencyTestThread.StartMany(3, r =>
            {
                var random = new Random(r);
                int seen;
                while ((seen = Volatile.Read(ref watermark)) < Bits)
                {
                    if (seen == 0)
                        continue;

                    var index = random.Next(seen);
                    Assert.True(bits.Get(index), $"bit {index} below watermark {seen} not visible");
                }
            });

            writer.Join();
            ConcurrencyTestThread.JoinAll(readers);
        }

    }

}
