using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Running;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Leaves out the cases of <see cref="WritesBenchmarks"/> whose <see cref="WritesBenchmarks.Shards"/> asks for the
    /// shard count the thread count gives anyway: at 8 threads, 16 shards are the default, so the case of
    /// <c>Shards = 16</c> would repeat the case of <c>Shards = 0</c>.
    /// </summary>
    internal sealed class DistinctShardCountsAttribute : FilterConfigBaseAttribute
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public DistinctShardCountsAttribute()
            : base(new SimpleFilter(IsDistinct))
        {

        }

        /// <summary>
        /// Gets whether a case's table layout differs from every other case's of the same thread count
        /// (<see cref="WritesShardCounts.IsDistinct"/>). A case without both parameters is run.
        /// </summary>
        /// <param name="benchmarkCase">The case.</param>
        /// <returns><c>true</c> to run the case.</returns>
        static bool IsDistinct(BenchmarkCase benchmarkCase)
        {
            if (benchmarkCase.Parameters[nameof(WritesBenchmarks.Shards)] is not int shards
                || benchmarkCase.Parameters[nameof(WritesBenchmarks.Threads)] is not int threads)
                return true;

            return WritesShardCounts.IsDistinct(threads, shards);
        }

    }

}
