using System.Linq;

using Eggnog.Relations.Benchmarks;

using Xunit;

namespace Eggnog.Tests.Benchmarks
{

    /// <summary>
    /// <see cref="WritesShardCounts"/>: the shard count each case of the writes benchmarks must build, and which cases
    /// the class runs.
    /// </summary>
    public sealed class WritesShardCountsTests
    {

        [Theory]
        [InlineData(1, 0, 1)]
        [InlineData(2, 0, 4)]
        [InlineData(4, 0, 8)]
        [InlineData(8, 0, 16)]
        [InlineData(1, 16, 16)]
        [InlineData(8, 16, 16)]
        [InlineData(4, 3, 4)]
        public void TheShardCountIsTheRequestOrTheDefault(int threads, int shards, int expected)
        {
            Assert.Equal(expected, WritesShardCounts.Expected(threads, shards));
        }

        [Theory]
        [InlineData(1, 0, true)]
        [InlineData(8, 0, true)]
        [InlineData(1, 16, true)]
        [InlineData(2, 16, true)]
        [InlineData(4, 16, true)]
        [InlineData(8, 16, false)]
        public void ACaseRepeatingTheDefaultLayoutIsLeftOut(int threads, int shards, bool distinct)
        {
            Assert.Equal(distinct, WritesShardCounts.IsDistinct(threads, shards));
        }

        [Fact]
        public void TheClassRunsSevenLayoutsPerMethod()
        {
            var cases = new[] { 1, 2, 4, 8 }.SelectMany(t => new[] { 0, 16 }.Select(s => (t, s)));
            Assert.Equal(7, cases.Count(c => WritesShardCounts.IsDistinct(c.t, c.s)));
        }

    }

}
