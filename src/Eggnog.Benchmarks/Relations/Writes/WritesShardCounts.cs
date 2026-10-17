using System.Numerics;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The shard counts of <c>WritesBenchmarks</c>' tables, from its <c>Threads</c> and <c>Shards</c> parameters,
    /// and which of its cases they make distinct.
    /// </summary>
    internal static class WritesShardCounts
    {

        /// <summary>
        /// Gets the shard count a table built for <paramref name="threads"/> and <paramref name="shards"/> must have: the
        /// requested count rounded up to a power of two, or, for zero, one shard at one thread and otherwise twice the
        /// thread count rounded up to a power of two.
        /// </summary>
        /// <param name="threads">The thread count.</param>
        /// <param name="shards">The requested shard count; zero for the default.</param>
        /// <returns>The shard count.</returns>
        public static int Expected(int threads, int shards)
        {
            var minimum = shards != 0 ? shards : threads <= 1 ? 1 : 2 * threads;
            return (int)BitOperations.RoundUpToPowerOf2((uint)minimum);
        }

        /// <summary>
        /// Gets whether a case's table layout differs from every other case's of the same thread count: its shard count
        /// is the default, or one the default does not give.
        /// </summary>
        /// <param name="threads">The thread count.</param>
        /// <param name="shards">The requested shard count; zero for the default.</param>
        /// <returns><c>true</c> to run the case.</returns>
        public static bool IsDistinct(int threads, int shards) =>
            shards == 0 || Expected(threads, shards) != Expected(threads, 0);

    }

}
