using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="ParallelHeuristics"/>' shard count: by default one shard serially and otherwise twice the degree of
    /// parallelism, rounded up to a power of two; or a count given apart from the degree, as egglog's benches get when
    /// they build a table outside the pool they install. A table's contents and change reports do not depend on it.
    /// </summary>
    public sealed class ParallelHeuristicsTests
    {

        const int Large = 30_000;

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 4)]
        [InlineData(3, 8)]
        [InlineData(4, 8)]
        [InlineData(8, 16)]
        public void TheDefaultShardCountFollowsTheDegree(int degree, int shards)
        {
            Assert.Equal(shards, new ParallelHeuristics(degree).ShardData.Count);
            Assert.Equal(shards, new ParallelHeuristics(degree, 0).ShardData.Count);
        }

        [Theory]
        [InlineData(1, 16, 16)]
        [InlineData(2, 16, 16)]
        [InlineData(4, 3, 4)]
        [InlineData(8, 1, 1)]
        [InlineData(1, 65_536, 65_536)]
        public void AGivenShardCountIsRoundedUpWhateverTheDegree(int degree, int requested, int shards)
        {
            var heuristics = new ParallelHeuristics(degree, requested);
            Assert.Equal(shards, heuristics.ShardData.Count);
            Assert.Equal(degree, heuristics.DegreeOfParallelism);
            Assert.Equal(degree, heuristics.Options.MaxDegreeOfParallelism);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, -1)]
        [InlineData(1, 65_537)]
        public void BadArgumentsAreRejected(int degree, int shards)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelHeuristics(degree, shards));
        }

        [Fact]
        public void TheMostShardsAreTwoToTheSixteenth()
        {
            // Below the top 7 bits of a 64-bit hash, 16 bits pick the shard and the low 41 a slot within it.
            Assert.Equal(65_536, ParallelHeuristics.MaxShards);
        }

        [Fact]
        public void ATableTakesTheShardCountOfItsHeuristics()
        {
            var table = new SortedWritesTable(1, 3, new ColumnId(2), ReadOnlySpan<ColumnId>.Empty, new MinMerge(new ColumnId(1)), new ParallelHeuristics(1, 16));
            Assert.Equal(16, table.Parallelism.ShardData.Count);
        }

        [Fact]
        public void ContentsAndChangesDoNotDependOnTheShardCount()
        {
            var configurations = new[] { new ParallelHeuristics(1), new ParallelHeuristics(1, 16), new ParallelHeuristics(4, 1), new ParallelHeuristics(4, 16), new ParallelHeuristics(4) };
            var results = new List<(Dictionary<uint, (uint, uint)> Contents, List<(TableChange, bool)> Reports)>();
            foreach (var parallelism in configurations)
            {
                var random = new Random(7);
                var catalog = new TableCatalog();
                var table = new SortedWritesTable(1, 3, new ColumnId(2), ReadOnlySpan<ColumnId>.Empty, new MinMerge(new ColumnId(1)), parallelism);
                catalog.Add(table);
                var reports = new List<(TableChange, bool)>();

                // Fresh keys; then colliding inserts and removals above the parallel threshold; then a small batch.
                foreach (var (ts, inserts, removals) in new[] { (1u, Large, 0), (2u, 2 * Large, Large / 3), (3u, 500, 100) })
                {
                    var buffers = Enumerable.Range(0, 3).Select(_ => table.NewBuffer()).ToArray();
                    for (var i = 0; i < inserts; i++)
                        buffers[i % 3].StageInsert([ts == 1 ? (uint)i : (uint)random.Next(2 * Large), (uint)random.Next(1000), ts]);

                    for (var i = 0; i < removals; i++)
                        buffers[i % 3].StageRemove([(uint)random.Next(2 * Large)]);

                    foreach (var buffer in buffers)
                        buffer.Flush();

                    var state = new ExecutionState(catalog, ts);
                    var change = table.Merge(state);
                    state.Flush();
                    reports.Add((change, state.Changed));
                }

                var contents = new Dictionary<uint, (uint, uint)>();
                foreach (var row in TableTestUtil.LiveRows(table))
                    Assert.True(contents.TryAdd(row[0], (row[1], row[2])), "duplicate key " + row[0]);

                results.Add((contents, reports));
            }

            foreach (var (contents, reports) in results.Skip(1))
            {
                Assert.Equal(results[0].Contents.Count, contents.Count);
                Assert.All(results[0].Contents, p => Assert.Equal(p.Value, contents[p.Key]));
                Assert.Equal(results[0].Reports, reports);
            }
        }

    }

}
