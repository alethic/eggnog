using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// How a sharded structure maps a 64-bit hash to a shard: by the bits just below the top seven, so that the low
    /// bits stay free for picking a slot within the shard and the top seven for a control byte.
    /// </summary>
    internal readonly struct ShardData
    {

        readonly int log2;

        /// <summary>
        /// Initializes a new instance with at least <paramref name="shards"/> shards, rounded up to a power of two.
        /// </summary>
        /// <param name="shards">The minimum number of shards.</param>
        public ShardData(int shards)
        {
            if (shards < 1)
                throw new ArgumentOutOfRangeException(nameof(shards));

            log2 = BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)shards));
        }

        /// <summary>
        /// Gets the number of shards.
        /// </summary>
        public int Count => 1 << log2;

        /// <summary>
        /// Gets the base-2 logarithm of the shard count.
        /// </summary>
        public int Log2 => log2;

        /// <summary>
        /// Gets the shard a hash belongs to.
        /// </summary>
        /// <param name="hash">The 64-bit hash.</param>
        /// <returns>The shard index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ShardOf(ulong hash) => log2 == 0 ? 0 : (int)((hash >> (64 - (log2 + 7))) & (ulong)((1 << log2) - 1));

        /// <summary>
        /// Gets the default shard count for tables: one when running single-threaded, otherwise twice the degree of
        /// parallelism, rounded up to a power of two.
        /// </summary>
        /// <param name="degreeOfParallelism">The degree of parallelism.</param>
        /// <returns>The shard layout.</returns>
        public static ShardData ForParallelism(int degreeOfParallelism) => new(degreeOfParallelism <= 1 ? 1 : degreeOfParallelism * 2);

    }

}
