using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="OffsetBufferPool"/>.
    /// </summary>
    public class OffsetBufferPoolTests
    {

        [Fact]
        public void RentZeroIsSharedEmptyArrayAndReturnIgnoresIt()
        {
            var empty = OffsetBufferPool.Rent(0);
            Assert.Same(Array.Empty<int>(), empty);
            OffsetBufferPool.Return(empty);
        }

        [Fact]
        public void RentCopyCopiesRunToFront()
        {
            var buffer = OffsetBufferPool.RentCopy([9, 1, 2, 3, 9], 1, 4);
            Assert.True(buffer.Length >= 3);
            Assert.Equal([1, 2, 3], buffer[..3]);
            OffsetBufferPool.Return(buffer);
        }

        [Fact]
        public void GrowForAppendCompactsWhenMostlyFree()
        {
            var buffer = OffsetBufferPool.Rent(16);
            var length = buffer.Length;
            var start = length - 3;
            var end = length;
            buffer[start] = 7;
            buffer[start + 1] = 8;
            buffer[start + 2] = 9;
            var grown = OffsetBufferPool.GrowForAppend(buffer, ref start, ref end);
            Assert.Same(buffer, grown);
            Assert.Equal(0, start);
            Assert.Equal(3, end);
            Assert.Equal([7, 8, 9], grown[..3]);
            OffsetBufferPool.Return(grown);
        }

        [Fact]
        public void GrowForAppendDoublesWhenFull()
        {
            var buffer = OffsetBufferPool.Rent(16);
            var length = buffer.Length;
            for (var i = 0; i < length; i++)
                buffer[i] = i;

            var start = 0;
            var end = length;
            var grown = OffsetBufferPool.GrowForAppend(buffer, ref start, ref end);
            Assert.NotSame(buffer, grown);
            Assert.True(grown.Length >= 2 * length);
            Assert.Equal(0, start);
            Assert.Equal(length, end);
            for (var i = 0; i < length; i++)
                Assert.Equal(i, grown[i]);

            OffsetBufferPool.Return(grown);
        }

        [Fact]
        public void GrowForAppendFromEmptyArrayRents()
        {
            var start = 0;
            var end = 0;
            var grown = OffsetBufferPool.GrowForAppend(Array.Empty<int>(), ref start, ref end);
            Assert.NotEmpty(grown);
            Assert.Equal(0, end);
            OffsetBufferPool.Return(grown);
        }

    }

}
