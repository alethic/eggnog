using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A thread-safe table that interns keys as dense <see cref="uint"/> ids, assigned in the order keys are first
    /// seen: the port of egglog's <c>InternTable</c> (core-relations/src/common.rs), which backs base values (strings,
    /// big integers, rationals) that do not fit in a table cell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keys live once, in an append-only store indexed by id (egglog's <c>ConcurrentVec</c>). The id lookup is
    /// sharded: each of the 16 shards is a <see cref="RawHashTable{TEntry}"/> of ids behind its own lock, so
    /// concurrent interning only contends when two keys land in the same shard. Shard tables store bare ids (4 bytes a
    /// slot) and recompute a key's hash from the store when they grow, as egglog does.
    /// </para>
    /// <para>
    /// Appends to the key store are serialized by a lock; reads never lock. The store grows by copying into a new array
    /// and publishing it; a reader holding an older array still sees every element it was given an id for, because
    /// elements are never written after their id is published.
    /// </para>
    /// <para>
    /// Costs: <see cref="Intern"/> is one key hash, one shard lock and one expected-O(1) probe, plus an amortized O(1)
    /// append for a new key. <see cref="TryGetId"/> is the same lookup without the append. <see cref="Get"/> and
    /// <see cref="GetRef"/> are two volatile reads and an array access.
    /// </para>
    /// </remarks>
    /// <typeparam name="TKey">The key type.</typeparam>
    internal sealed class InternTable<TKey>
        where TKey : notnull, IEquatable<TKey>
    {

        /// <summary>
        /// egglog's default shard count (<c>shards_log2 = 4</c>).
        /// </summary>
        const int DefaultShards = 16;

        /// <summary>
        /// egglog's initial key-store capacity (<c>ConcurrentVec::with_capacity(512)</c>).
        /// </summary>
        const int InitialCapacity = 512;

        readonly ShardData shardData;
        readonly RawHashTable<uint>[] shards;
        readonly object pushLock = new();
        TKey[] keys;
        int count;

        /// <summary>
        /// Initializes a new, empty instance with egglog's 16 shards.
        /// </summary>
        public InternTable()
            : this(DefaultShards)
        {

        }

        /// <summary>
        /// Initializes a new, empty instance with at least <paramref name="shards"/> shards, rounded up to a power of
        /// two.
        /// </summary>
        /// <param name="shards">The minimum number of shards.</param>
        internal InternTable(int shards)
        {
            shardData = new ShardData(shards);
            this.shards = new RawHashTable<uint>[shardData.Count];
            for (var i = 0; i < this.shards.Length; i++)
                this.shards[i] = new RawHashTable<uint>();

            keys = new TKey[InitialCapacity];
        }

        /// <summary>
        /// Gets the number of interned keys; ids are <c>[0, Count)</c>.
        /// </summary>
        public int Count => Volatile.Read(ref count);

        /// <summary>
        /// Gets the id of <paramref name="key"/>, assigning the next id if the key is new. Thread-safe; equal keys
        /// interned concurrently get the same id.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The id.</returns>
        public uint Intern(TKey key)
        {
            if (key is null)
                throw new ArgumentNullException(nameof(key));

            // egglog picks the shard from the top bits of the hash (core-relations/src/common.rs:58), which are also
            // hashbrown's 7-bit tag, so every id in a shard shares the top tag bits and tag compares filter worse.
            // ShardData uses the bits just below the tag instead. Ids are assigned by append order either way.
            var hash = HashKey(key);
            var shard = shards[shardData.ShardOf(hash)];

            lock (shard)
            {
                // Every id in this shard was appended by a thread that held this lock, before it released it, so a
                // snapshot of the store read now holds all of them.
                var match = new InternKeyMatch<TKey>(Volatile.Read(ref keys), key);
                var slot = shard.Find(hash, ref match);
                if (slot >= 0)
                    return shard.GetEntry(slot);

                var id = Push(key);
                shard.InsertUnique(hash, id, new InternKeyHasher<TKey>(Volatile.Read(ref keys)));
                return id;
            }
        }

        /// <summary>
        /// Gets the id of <paramref name="key"/> if it has been interned, without interning it. egglog has no such
        /// member; it serves host reads, which must not grow a pool with values they only look up (rule-layer D15,
        /// request E1). Thread-safe; never appends to the store.
        /// </summary>
        /// <remarks>
        /// Costs one key hash, the key's shard lock and one expected-O(1) probe, as <see cref="Intern"/> does for a key
        /// that is present. The lock is needed because a shard table may be growing on another thread; it orders this
        /// lookup with every <see cref="Intern"/> of the same shard, so a key interned before the call is found, and a
        /// key interned concurrently is either found with its final id or not found.
        /// </remarks>
        /// <param name="key">The key.</param>
        /// <param name="id">The key's id when this returns <c>true</c>; otherwise 0.</param>
        /// <returns>Whether the key has been interned.</returns>
        public bool TryGetId(TKey key, out uint id)
        {
            if (key is null)
                throw new ArgumentNullException(nameof(key));

            var hash = HashKey(key);
            var shard = shards[shardData.ShardOf(hash)];

            lock (shard)
            {
                // As in Intern: every id in this shard was appended under this lock, so this snapshot holds them all.
                var match = new InternKeyMatch<TKey>(Volatile.Read(ref keys), key);
                var slot = shard.Find(hash, ref match);
                if (slot >= 0)
                {
                    id = shard.GetEntry(slot);
                    return true;
                }
            }

            id = 0;
            return false;
        }

        /// <summary>
        /// Gets the key interned as <paramref name="id"/>. egglog's <c>get_cloned</c>. Lock-free.
        /// </summary>
        /// <param name="id">An id returned by <see cref="Intern"/>.</param>
        /// <returns>The key.</returns>
        public TKey Get(uint id) => GetRef(id);

        /// <summary>
        /// Gets a read-only reference to the key interned as <paramref name="id"/>. egglog's <c>get</c>. Lock-free;
        /// the reference stays valid (the stored key never changes), even after the store grows.
        /// </summary>
        /// <param name="id">An id returned by <see cref="Intern"/>.</param>
        /// <returns>A reference to the key.</returns>
        public ref readonly TKey GetRef(uint id)
        {
            // Count first: its acquire read orders the array read after it, so the array is at least as new as the
            // one the id was written to.
            if (id >= (uint)Volatile.Read(ref count))
                throw new ArgumentOutOfRangeException(nameof(id), $"No key is interned as {id}.");

            return ref Volatile.Read(ref keys)[id];
        }

        /// <summary>
        /// Gets the 64-bit hash of a key, spread by <see cref="FxHasher"/> so that the tag bits (top seven), the shard
        /// bits (just below) and the probe bits (low) all vary.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The 64-bit primitives egglog interns (<c>i64</c>, <c>u64</c>, <c>isize</c>, <c>usize</c>: the medium base
        /// values of core-relations/src/base_values/unboxed.rs, and <c>f64</c>) are hashed by value, as Rust's
        /// <c>FxHasher::write_u64</c> does: one multiply by an odd constant and a rotate, a bijection, so distinct keys
        /// never share a hash. Their <see cref="object.GetHashCode"/> folds the two halves together (<c>lo ^ hi</c>),
        /// which maps whole families of values (every <c>k * (2^32 + 1)</c>, say) to one hash and would make
        /// interning them quadratic. A <see cref="double"/> is hashed by its bits with NaNs and signed zeros
        /// canonicalized, as <see cref="double.Equals(double)"/> treats them as equal.
        /// </para>
        /// <para>
        /// Other keys use their <see cref="object.GetHashCode"/> spread over 64 bits. That is exact for the 32-bit and
        /// smaller primitives (it is <c>FxHasher::write_u32</c> for <see cref="int"/> and <see cref="uint"/>), and
        /// leaves the quality of composite keys' hashes to the key type.
        /// </para>
        /// <para>
        /// The type tests are JIT-time constants: each instantiation compiles to just its own branch.
        /// </para>
        /// </remarks>
        /// <param name="key">The key.</param>
        /// <returns>The hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong HashKey(TKey key)
        {
            if (typeof(TKey) == typeof(long))
                return HashWord((ulong)Unsafe.As<TKey, long>(ref key));

            if (typeof(TKey) == typeof(ulong))
                return HashWord(Unsafe.As<TKey, ulong>(ref key));

            if (typeof(TKey) == typeof(nint))
                return HashWord((ulong)(long)Unsafe.As<TKey, nint>(ref key));

            if (typeof(TKey) == typeof(nuint))
                return HashWord((ulong)Unsafe.As<TKey, nuint>(ref key));

            if (typeof(TKey) == typeof(double))
            {
                var value = Unsafe.As<TKey, double>(ref key);
                var bits = BitConverter.DoubleToUInt64Bits(value);

                // All NaNs become the positive-infinity exponent pattern and both zeros become 0, as in
                // double.GetHashCode, so keys double.Equals calls equal hash equally.
                if (double.IsNaN(value) || value == 0)
                    bits &= 0x7FF0_0000_0000_0000UL;

                return HashWord(bits);
            }

            return FxHasher.Hash((uint)key.GetHashCode());
        }

        /// <summary>
        /// Hashes one 64-bit word the way Rust's <c>FxHasher</c> hashes a <c>u64</c>: <c>write_u64</c> on a fresh
        /// hasher, then <c>finish</c>.
        /// </summary>
        /// <param name="word">The word.</param>
        /// <returns>The hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong HashWord(ulong word)
        {
            var hasher = new FxHasher();
            hasher.Write(word);
            return hasher.Finish();
        }

        /// <summary>
        /// Appends a key to the store and publishes it. egglog's <c>ConcurrentVec::push</c>.
        /// </summary>
        uint Push(TKey key)
        {
            lock (pushLock)
            {
                var index = count;
                var store = keys;
                if (index == store.Length)
                {
                    if (index == Array.MaxLength)
                        throw new InvalidOperationException("The intern table is full.");

                    // Copy, then publish: readers that still hold the old array keep valid, never-again-written
                    // elements.
                    var grown = new TKey[(int)Math.Min((long)store.Length * 2, Array.MaxLength)];
                    Array.Copy(store, grown, index);
                    Volatile.Write(ref keys, grown);
                    store = grown;
                }

                store[index] = key;

                // Release: a reader that sees the new count also sees the element and the array holding it.
                Volatile.Write(ref count, index + 1);
                return (uint)index;
            }
        }

    }

}
