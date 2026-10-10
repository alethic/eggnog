using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    public class IdentityFillTests
    {

        [Theory]
        [InlineData(0, 0u)]
        [InlineData(1, 0u)]
        [InlineData(7, 3u)]
        [InlineData(15, 100u)]
        [InlineData(16, 0u)]
        [InlineData(17, 5u)]
        [InlineData(33, 1u)]
        [InlineData(1000, 12345u)]
        [InlineData(1031, 32u)]
        public void FillsAscendingIds(int length, uint first)
        {
            var buffer = new uint[length + 2];
            IdentityFill.Fill(buffer.AsSpan(1, length), first);
            Assert.Equal(0u, buffer[0]);
            Assert.Equal(0u, buffer[^1]);
            Assert.Equal(Enumerable.Range(0, length).Select(i => first + (uint)i), buffer.Skip(1).Take(length));
        }

    }

}
