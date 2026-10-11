using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="OffsetBufferCache"/>, the per-thread stock of small offset buffers in front of the shared
    /// array pool, and for <see cref="OffsetBufferPool"/> with many small buffers live at once.
    /// </summary>
    public class OffsetBufferCacheTests
    {

        [Fact]
        public void BuffersComeBackLastInFirstOutByLength()
        {
            var cache = new OffsetBufferCache();
            Assert.Null(cache.TryRent(1));
            var a = new int[16];
            var b = new int[16];
            var c = new int[32];
            Assert.True(cache.TryReturn(a));
            Assert.True(cache.TryReturn(b));
            Assert.True(cache.TryReturn(c));
            Assert.Equal(64 * sizeof(int), cache.Bytes);

            // A request is served by the length the shared pool would hand out for it.
            Assert.Same(c, cache.TryRent(17));
            Assert.Null(cache.TryRent(32));
            Assert.Same(b, cache.TryRent(1));
            Assert.Same(a, cache.TryRent(16));
            Assert.Null(cache.TryRent(16));
            Assert.Equal(0, cache.Bytes);
        }

        [Fact]
        public void OnlyPoolLengthsAreKept()
        {
            var cache = new OffsetBufferCache();
            Assert.False(cache.TryReturn(new int[8]));
            Assert.False(cache.TryReturn(new int[24]));
            Assert.False(cache.TryReturn(new int[OffsetBufferCache.MaxLength * 2]));
            Assert.True(cache.TryReturn(new int[OffsetBufferCache.MaxLength]));
            Assert.NotNull(cache.TryRent(OffsetBufferCache.MaxLength));
        }

        [Fact]
        public void EachLengthHasItsOwnBudget()
        {
            var cache = new OffsetBufferCache();
            var longest = OffsetBufferCache.CapacityFor(OffsetBufferCache.MaxLength);
            Assert.Equal((int)(PoolLimits.OffsetVec / (OffsetBufferCache.MaxLength * sizeof(int))), longest);
            for (var i = 0; i < longest; i++)
                Assert.True(cache.TryReturn(new int[OffsetBufferCache.MaxLength]));

            Assert.False(cache.TryReturn(new int[OffsetBufferCache.MaxLength]));
            Assert.Equal(PoolLimits.OffsetVec, cache.Bytes);

            // Long buffers filling their budget do not crowd out short ones.
            Assert.True(cache.TryReturn(new int[16]));
            Assert.Equal(16384, OffsetBufferCache.CapacityFor(16));
        }

        /// <summary>
        /// More small buffers live at once than the shared array pool holds per length (a few dozen per core), as when a
        /// join buffers a morsel of frames that each own refined subsets: once returned, they are rented again without
        /// allocating.
        /// </summary>
        [Fact]
        public void ManyLiveSmallBuffersAreReusedWithoutAllocating()
        {
            const int Live = 3000;
            var buffers = new int[Live][];
            for (var round = 0; round < 2; round++)
            {
                for (var i = 0; i < Live; i++)
                    buffers[i] = OffsetBufferPool.Rent(10);

                for (var i = 0; i < Live; i++)
                    OffsetBufferPool.Return(buffers[i]);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Live; i++)
                buffers[i] = OffsetBufferPool.Rent(10);

            for (var i = 0; i < Live; i++)
                OffsetBufferPool.Return(buffers[i]);

            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(bytes < 1024, $"{bytes} bytes for {Live} live buffers");
        }

    }

}
