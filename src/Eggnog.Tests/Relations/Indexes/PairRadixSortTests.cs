using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="PairRadixSort"/>, egglog's <c>radix_sort_pairs_by_value</c> over packed (value, row) pairs.
    /// </summary>
    public class PairRadixSortTests
    {

        /// <summary>
        /// Pairs in ascending row order, as the index build emits them, with values of every byte width and both sides
        /// of the comparison-sort threshold, come out sorted by (value, row).
        /// </summary>
        [Theory]
        [InlineData(0, 0u)]
        [InlineData(10, 200u)]
        [InlineData(63, 70_000u)]
        [InlineData(64, 0u)]
        [InlineData(64, 255u)]
        [InlineData(500, 256u)]
        [InlineData(1000, 65_535u)]
        [InlineData(1000, 65_536u)]
        [InlineData(3000, 16_777_215u)]
        [InlineData(3000, 16_777_216u)]
        [InlineData(5000, uint.MaxValue)]
        public void SortsRowOrderedPairsByValueThenRow(int count, uint maxValue)
        {
            var random = new Random(count ^ (int)maxValue);
            var pairs = new ulong[count + 5];
            var row = 0;
            for (var i = 0; i < count; i++)
            {
                row += random.Next(0, 3);
                var value = maxValue == 0 ? 0u : (uint)(random.NextInt64(0, (long)maxValue + 1));
                pairs[i] = ColumnPairSink.Pack(value, row);
            }

            pairs[count] = 42;
            var expected = pairs.Take(count).OrderBy(x => x).ToArray();
            PairRadixSort.SortByValue(pairs, count);
            Assert.Equal(expected, pairs.Take(count));
            Assert.Equal(42UL, pairs[count]);
        }

        [Fact]
        public void SkipsPassesWhoseByteIsConstant()
        {
            // Every value shares its low byte, and the second byte varies: the result is still sorted.
            var pairs = Enumerable.Range(0, 300).Select(i => ColumnPairSink.Pack((uint)((299 - i) << 8 | 0x5A), i)).ToArray();
            var expected = pairs.OrderBy(x => x).ToArray();
            PairRadixSort.SortByValue(pairs, pairs.Length);
            Assert.Equal(expected, pairs);
        }

        [Fact]
        public void RejectsACountPastTheArray()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PairRadixSort.SortByValue(new ulong[3], 4));
        }

    }

}
