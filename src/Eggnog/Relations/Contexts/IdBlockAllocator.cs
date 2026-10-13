using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// Mints a child context's ids from blocks reserved on the store's shared id counter, and answers exactly whether
    /// an id was minted here. Every id a child mints (terms through <see cref="Database.IncrementCounterInView"/>,
    /// <see cref="Context.NewId"/>) comes from its blocks, so <see cref="Contains"/> is
    /// <see cref="Context.IsLocalId"/>, with no per-id memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Next"/> takes an id from the current block with one interlocked increment. When the block is spent,
    /// one thread reserves the next block under a lock with <see cref="CounterSet.IncrementBy"/>: its size is twice the
    /// ids reserved so far, clamped to [<see cref="FirstBlockSize"/>, <see cref="MaxBlockSize"/>], so a child that
    /// mints one id reserves 16 and a busy child's waste stays below half of what it minted (or 4096). The blocks of
    /// different allocators and the root's own mints come from one counter, so they are disjoint.
    /// </para>
    /// <para>
    /// The blocks are kept as one copy-on-write array of <c>[start, end)</c> pairs in ascending order (the counter only
    /// grows), published before the block that uses it, so any thread that holds a minted id finds its block.
    /// <see cref="Contains"/> is a binary search over it, O(log blocks), lock-free. An id inside a reserved block that
    /// was never handed out also reports <c>true</c>, which is harmless: it appears nowhere.
    /// </para>
    /// <para>
    /// Ids stay at or below <see cref="UnionFind.MaxId"/>, the largest id a union-find array can hold: a block that
    /// would reach past it throws <see cref="OverflowException"/> (the counter has advanced by then; those ids leak).
    /// </para>
    /// </remarks>
    internal sealed class IdBlockAllocator
    {

        /// <summary>
        /// The size of the first block, and the smallest block.
        /// </summary>
        public const int FirstBlockSize = 16;

        /// <summary>
        /// The largest block.
        /// </summary>
        public const int MaxBlockSize = 4096;

        readonly CounterSet counters;
        readonly CounterId counter;
        readonly object sync = new();

        /// <summary>
        /// The block ids are taken from; replaced under <see cref="sync"/> when spent. Starts empty, so the first
        /// <see cref="Next"/> reserves.
        /// </summary>
        IdBlock current = new(0, 0, 0);

        /// <summary>
        /// Every block reserved, as <c>[start0, end0, start1, end1, ...]</c> in ascending order; replaced, never
        /// changed in place.
        /// </summary>
        long[] bounds = Array.Empty<long>();

        /// <summary>
        /// Initializes an allocator that reserves blocks on <paramref name="counter"/> of <paramref name="counters"/>.
        /// Reserves nothing until the first <see cref="Next"/>.
        /// </summary>
        /// <param name="counters">The store's counters.</param>
        /// <param name="counter">The store's id counter.</param>
        public IdBlockAllocator(CounterSet counters, CounterId counter)
        {
            ArgumentNullException.ThrowIfNull(counters);
            this.counters = counters;
            this.counter = counter;
        }

        /// <summary>
        /// Gets the counter blocks are reserved on.
        /// </summary>
        public CounterId Counter => counter;

        /// <summary>
        /// Gets the number of ids handed out so far. A snapshot while other threads mint.
        /// </summary>
        public long Minted
        {
            get
            {
                var block = Volatile.Read(ref current);
                return block.ReservedBefore + Math.Min(Volatile.Read(ref block.Next), block.End) - block.Start;
            }
        }

        /// <summary>
        /// Gets the number of blocks reserved.
        /// </summary>
        public int BlockCount => Volatile.Read(ref bounds).Length / 2;

        /// <summary>
        /// Gets the first id of block <paramref name="block"/>, in reservation order.
        /// </summary>
        /// <param name="block">The block, below <see cref="BlockCount"/>.</param>
        /// <returns>The first id.</returns>
        public long BlockStart(int block) => Volatile.Read(ref bounds)[2 * block];

        /// <summary>
        /// Gets one past the last id of block <paramref name="block"/>, in reservation order.
        /// </summary>
        /// <param name="block">The block, below <see cref="BlockCount"/>.</param>
        /// <returns>The end of the block.</returns>
        public long BlockEnd(int block) => Volatile.Read(ref bounds)[2 * block + 1];

        /// <summary>
        /// Mints a fresh id. Thread-safe; one interlocked increment unless the current block is spent.
        /// </summary>
        /// <returns>The id.</returns>
        /// <exception cref="OverflowException">The next block would hold an id above <see cref="UnionFind.MaxId"/>.</exception>
        public uint Next()
        {
            while (true)
            {
                var block = Volatile.Read(ref current);
                var value = Interlocked.Increment(ref block.Next) - 1;
                if (value < block.End)
                    return (uint)value;

                Reserve(block);
            }
        }

        /// <summary>
        /// Gets whether <paramref name="id"/> lies in a block this allocator reserved. Lock-free, O(log blocks).
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if the id is local to the allocator's context.</returns>
        public bool Contains(uint id)
        {
            var b = Volatile.Read(ref bounds);
            var lo = 0;
            var hi = b.Length / 2 - 1;

            // The last block whose start is at most id.
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                if (b[2 * mid] <= id)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }

            return hi >= 0 && id < b[2 * hi + 1];
        }

        /// <summary>
        /// Replaces <paramref name="spent"/> with a fresh block, unless another thread already did.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void Reserve(IdBlock spent)
        {
            lock (sync)
            {
                if (ReferenceEquals(current, spent) == false)
                    return;

                // Every earlier block is spent when the current one is, so the ids reserved are the ids minted.
                var reserved = spent.ReservedBefore + (spent.End - spent.Start);
                var size = Math.Clamp(2 * reserved, FirstBlockSize, MaxBlockSize);
                var start = counters.IncrementBy(counter, size);
                if (start + size - 1 > UnionFind.MaxId)
                    ThrowExhausted(start, size);

                var old = bounds;
                var grown = new long[old.Length + 2];
                Array.Copy(old, grown, old.Length);
                grown[old.Length] = start;
                grown[old.Length + 1] = start + size;

                // The bounds first: any id of the new block is minted after the block is published, so a thread that
                // sees the id sees its bounds.
                Volatile.Write(ref bounds, grown);
                Volatile.Write(ref current, new IdBlock(start, start + size, reserved));
            }
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowExhausted(long start, long size)
        {
            throw new OverflowException($"The store has run out of ids: a block of {size} ids at {start} would pass the largest id a union-find can hold, {UnionFind.MaxId}.");
        }

    }

}
