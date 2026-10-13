using System;
using System.Buffers;
using System.Numerics;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="PooledArray"/>.
    /// </summary>
    public class PooledArrayTests
    {

        [Fact]
        public void SmallArraysAreExactAndLargeOnesArePooledSizes()
        {
            Assert.Same(Array.Empty<uint>(), PooledArray.Allocate<uint>(0));
            Assert.Equal(5, PooledArray.Allocate<uint>(5).Length);
            Assert.Equal(256, PooledArray.Allocate<uint>(256).Length);

            var large = PooledArray.Allocate<uint>(257);
            Assert.True(large.Length >= 257);
            Assert.True(BitOperations.IsPow2(large.Length));
            PooledArray.Release(large);
        }

        [Fact]
        public void ReleaseIgnoresArraysThePoolCannotHold()
        {
            PooledArray.Release(new uint[10]);
            PooledArray.Release(new uint[300]);
            PooledArray.Release(Array.Empty<uint>());
        }

        [Fact]
        public void GrowKeepsThePrefixAndAtLeastDoubles()
        {
            var array = new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 };

            var grown = PooledArray.Grow(array, 5, 9);

            Assert.True(grown.Length >= 16);
            Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, grown.AsSpan(0, 5).ToArray());
        }

        [Fact]
        public void GrowHonoursTheRequiredLength()
        {
            var grown = PooledArray.Grow(Array.Empty<uint>(), 0, 1000);

            Assert.True(grown.Length >= 1000);
            PooledArray.Release(grown);
        }

        [Fact]
        public void GrowStartsAtTheMinimumLength()
        {
            var grown = PooledArray.Grow(Array.Empty<uint>(), 0, 1);

            Assert.Equal(PooledArray.MinGrowLength, grown.Length);
        }

        /// <summary>
        /// egglog keeps a vector only while its pool stays within 32 MiB (<c>PoolState::push</c>): a vector of
        /// exactly the limit is kept, a larger one never is.
        /// </summary>
        [Fact]
        public void OnlyArraysWithinTheByteLimitArePoolable()
        {
            Assert.Equal(PoolLimits.VecCellValues, PooledArray.MaxPooledBytes);

            Assert.False(PooledArray.IsPoolable<uint>(256));
            Assert.False(PooledArray.IsPoolable<uint>(300));
            Assert.True(PooledArray.IsPoolable<uint>(512));
            Assert.True(PooledArray.IsPoolable<uint>(1 << 23));
            Assert.False(PooledArray.IsPoolable<uint>(1 << 24));
            Assert.True(PooledArray.IsPoolable<ulong>(1 << 22));
            Assert.False(PooledArray.IsPoolable<ulong>(1 << 23));
            Assert.False(PooledArray.IsPoolable<uint>(-1));
        }

        /// <summary>
        /// An array too large to go back to the pool is allocated at exactly the length asked for, not rounded up to
        /// the pool's next power of two.
        /// </summary>
        [Fact]
        public void ArraysOverTheByteLimitAreAllocatedExactly()
        {
            var length = (int)(PooledArray.MaxPooledBytes / sizeof(uint)) + 1;

            var array = PooledArray.Allocate<uint>(length);

            Assert.Equal(length, array.Length);
            PooledArray.Release(array);
        }

        [Fact]
        public void ArraysWithinTheByteLimitComeFromThePool()
        {
            var length = (int)(PooledArray.MaxPooledBytes / sizeof(uint)) / 2 + 1;

            var array = PooledArray.Allocate<uint>(length);

            Assert.True(PooledArray.IsPoolable<uint>(array.Length));
            Assert.True(BitOperations.IsPow2(array.Length));
            PooledArray.Release(array);
        }

        /// <summary>
        /// egglog keeps row storage (<c>vec_cell_vals: Vec&lt;Cell&lt;Value&gt;&gt;</c>) in a pool capped at 32 MiB per
        /// thread; <c>PoolState::push</c> (pool/mod.rs:182-192) drops any vector that would push the pool past the cap,
        /// so a single vector over 32 MiB is freed when its buffer is dropped. <see cref="PooledArray"/> never returns
        /// an array over <see cref="PooledArray.MaxPooledBytes"/> to <see cref="ArrayPool{T}.Shared"/>, so disposed
        /// storage of that size is not kept reachable from the process-wide pool.
        /// </summary>
        [Fact]
        public void DisposedRowStorageOverTheVecCellLimitIsNotRetained()
        {
            var cells = (int)(PoolLimits.VecCellValues / sizeof(uint)) + 1;
            var rows = new RowBuffer(1, cells);
            var storage = rows.Data;
            Assert.True((long)storage.Length * sizeof(uint) > PoolLimits.VecCellValues);

            rows.Dispose();

            var again = ArrayPool<uint>.Shared.Rent(storage.Length);
            var retained = ReferenceEquals(storage, again);
            if (retained == false)
                ArrayPool<uint>.Shared.Return(again);

            Assert.False(retained, "A 64 MiB row buffer was kept by the shared pool after Dispose; egglog frees it.");
        }

    }

}
