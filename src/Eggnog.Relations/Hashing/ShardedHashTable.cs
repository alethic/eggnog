using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A hash table split into a power-of-two number of independent <see cref="RawHashTable{TEntry}"/> shards, chosen
    /// by <see cref="ShardData"/> from the bits just below an entry hash's 7-bit tag. The port of egglog's
    /// <c>ShardedHashTable</c> (core-relations/src/table/sharded_hash_table.rs).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sharding exists for parallel merges: writers pre-shard their pending rows with the same
    /// <see cref="ShardData"/>, and one task per shard then updates its shard with no locking. Because the shard bits
    /// sit between the tag bits (top seven) and the probe bits (low bits), entries of one shard still spread evenly
    /// over that shard's slots and tags.
    /// </para>
    /// <para>
    /// The shard count is fixed at construction. Not thread-safe as a whole; distinct shards may be used from distinct
    /// threads at the same time.
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntry">The entry type.</typeparam>
    internal sealed class ShardedHashTable<TEntry>
        where TEntry : struct
    {

        readonly ShardData shardData;
        readonly RawHashTable<TEntry>[] shards;

        /// <summary>
        /// Initializes a new instance with the given shard layout and empty shards.
        /// </summary>
        /// <param name="shardData">The shard layout.</param>
        public ShardedHashTable(ShardData shardData)
        {
            this.shardData = shardData;
            shards = new RawHashTable<TEntry>[shardData.Count];
            for (var i = 0; i < shards.Length; i++)
                shards[i] = new RawHashTable<TEntry>();
        }

        /// <summary>
        /// Initializes a new instance with at least <paramref name="shards"/> shards, rounded up to a power of two.
        /// egglog's <c>with_shards</c>.
        /// </summary>
        /// <param name="shards">The minimum number of shards.</param>
        public ShardedHashTable(int shards)
            : this(new ShardData(shards))
        {

        }

        ShardedHashTable(ShardData shardData, RawHashTable<TEntry>[] shards)
        {
            this.shardData = shardData;
            this.shards = shards;
        }

        /// <summary>
        /// Gets the shard layout, so that writers can pre-shard data bound for this table.
        /// </summary>
        public ShardData ShardData => shardData;

        /// <summary>
        /// Gets the number of shards.
        /// </summary>
        public int ShardCount => shards.Length;

        /// <summary>
        /// Gets a shard by index.
        /// </summary>
        /// <param name="index">The shard index, in <c>[0, ShardCount)</c>.</param>
        /// <returns>The shard.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RawHashTable<TEntry> GetShard(int index) => shards[index];

        /// <summary>
        /// Gets a shard by identifier. egglog's <c>get_shard</c> (and, since shards are mutable classes,
        /// <c>mut_shards</c>).
        /// </summary>
        /// <param name="shard">The shard.</param>
        /// <returns>The shard's table.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RawHashTable<TEntry> GetShard(ShardId shard) => shards[shard.Rep];

        /// <summary>
        /// Gets the shard an entry with <paramref name="hash"/> belongs to.
        /// </summary>
        /// <param name="hash">The entry's 64-bit hash.</param>
        /// <returns>The shard's table.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RawHashTable<TEntry> ShardFor(ulong hash) => shards[shardData.ShardOf(hash)];

        /// <summary>
        /// Removes every entry from every shard, keeping the allocations.
        /// </summary>
        public void Clear()
        {
            foreach (var shard in shards)
                shard.Clear();
        }

        /// <summary>
        /// Creates a deep copy: same layout, each shard cloned.
        /// </summary>
        /// <returns>The copy.</returns>
        public ShardedHashTable<TEntry> Clone()
        {
            var copy = new RawHashTable<TEntry>[shards.Length];
            for (var i = 0; i < shards.Length; i++)
                copy[i] = shards[i].Clone();

            return new ShardedHashTable<TEntry>(shardData, copy);
        }

    }

}
