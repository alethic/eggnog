using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A dense set of non-negative integers, one bit each, that any number of threads may read and update at once and
    /// that grows on demand. Reads and updates are wait-free; only growth takes a lock, and it never blocks readers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of egglog's <c>BitSet</c> (concurrency/src/bitset.rs). The Rust keeps a single <c>Vec&lt;AtomicU64&gt;</c>
    /// behind a <c>ReadOptimizedLock</c>, so every get and set takes a read lock and growth waits for all of them. Here
    /// the words live in segments that never move once allocated, and a directory of segments is republished on
    /// growth. A reader that sees an old directory still reaches the same words, so no update made during growth is
    /// lost and no lock is needed on the read or update path.
    /// </para>
    /// <para>
    /// Layout: with <c>B</c> words in segment 0, segment <c>k &gt; 0</c> holds the <c>B·2^(k-1)</c> words starting at
    /// word <c>B·2^(k-1)</c>, so the total size doubles with each segment exactly as the Rust's
    /// <c>(cell + 1).next_power_of_two()</c> resize does. <c>B</c> is the Rust's initial cell count:
    /// <c>capacity</c> rounded up to a multiple of 64 and then to a power of two, over 64 (at least 1).
    /// </para>
    /// <para>
    /// Memory ordering matches the Rust: <see cref="Get"/> is an acquire load and <see cref="Set"/> an interlocked
    /// read-modify-write, which is a full fence (the Rust uses release).
    /// </para>
    /// <para>
    /// Cost: <see cref="Get"/> and <see cref="Set"/> are a handful of ALU operations and three dependent loads; growth
    /// allocates the missing segments and a new directory under a lock.
    /// </para>
    /// </remarks>
    internal sealed class ConcurrentBitSet
    {

        readonly object growLock = new();

        /// <summary>
        /// The base-2 logarithm of the number of words in segment 0.
        /// </summary>
        readonly int log2Base;

        /// <summary>
        /// The segment directory. Replaced wholesale (under <see cref="growLock"/>) when it grows; segments are shared
        /// between successive directories and never reallocated.
        /// </summary>
        ulong[][] segments;

        /// <summary>
        /// Initializes a new instance with room for at least <paramref name="capacity"/> bits before it first grows.
        /// </summary>
        /// <param name="capacity">The number of bits to allocate up front.</param>
        public ConcurrentBitSet(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);

            // bitset.rs:17-25: n.next_multiple_of(64).next_power_of_two() / 64 cells.
            var words = (int)(BitOperations.RoundUpToPowerOf2((ulong)capacity + 63 & ~63UL) >> 6);
            log2Base = words == 0 ? 0 : BitOperations.Log2((uint)words);
            segments = words == 0 ? Array.Empty<ulong[]>() : [new ulong[words]];
        }

        /// <summary>
        /// Gets the number of bits currently allocated: one more than the largest index that can be set without
        /// growing.
        /// </summary>
        public long Capacity
        {
            get
            {
                var directory = Volatile.Read(ref segments);
                return directory.Length == 0 ? 0 : (long)SegmentStart(directory.Length) << 6;
            }
        }

        /// <summary>
        /// Gets the bit at <paramref name="index"/>. A bit beyond the allocated words is not set.
        /// </summary>
        /// <param name="index">The bit index.</param>
        /// <returns><c>true</c> if the bit is set.</returns>
        public bool Get(int index)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            var word = (uint)index >> 6;
            var segment = SegmentOf(word);
            var directory = Volatile.Read(ref segments);
            if (segment >= directory.Length)
                return false;

            return (Volatile.Read(ref directory[segment][word - SegmentStart(segment)]) & 1UL << index) != 0;
        }

        /// <summary>
        /// Sets the bit at <paramref name="index"/> to <paramref name="value"/>, growing the set first if the bit is not
        /// allocated yet. Concurrent updates to other bits of the same word are never lost.
        /// </summary>
        /// <param name="index">The bit index.</param>
        /// <param name="value">The new value.</param>
        public void Set(int index, bool value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            var word = (uint)index >> 6;
            var segment = SegmentOf(word);
            var directory = Volatile.Read(ref segments);
            if (segment >= directory.Length)
                directory = Grow(segment);

            ref var cell = ref directory[segment][word - SegmentStart(segment)];
            var mask = 1UL << index;
            if (value)
                Interlocked.Or(ref cell, mask);
            else
                Interlocked.And(ref cell, ~mask);
        }

        /// <summary>
        /// Gets the segment holding word <paramref name="word"/>.
        /// </summary>
        /// <param name="word">The word index.</param>
        /// <returns>The segment index: 0 for the first <c>B</c> words, then <c>floor(log2(word / B)) + 1</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int SegmentOf(uint word) => 32 - BitOperations.LeadingZeroCount(word >> log2Base);

        /// <summary>
        /// Gets the first word of segment <paramref name="segment"/>, which is also the number of words in all segments
        /// before it.
        /// </summary>
        /// <param name="segment">The segment index.</param>
        /// <returns>0 for segment 0, otherwise <c>B·2^(segment-1)</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint SegmentStart(int segment) => (1u << segment >> 1) << log2Base;

        /// <summary>
        /// Allocates every segment up to and including <paramref name="segment"/> and publishes the new directory.
        /// </summary>
        /// <param name="segment">The segment that must exist.</param>
        /// <returns>A directory that contains <paramref name="segment"/>.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        ulong[][] Grow(int segment)
        {
            lock (growLock)
            {
                var directory = segments;
                if (segment < directory.Length)
                    return directory;

                var grown = new ulong[segment + 1][];
                Array.Copy(directory, grown, directory.Length);
                for (var k = directory.Length; k <= segment; k++)
                    grown[k] = new ulong[k == 0 ? 1 << log2Base : (int)SegmentStart(k)];

                // Release: the new segments are zeroed before any thread can reach them through the directory.
                Volatile.Write(ref segments, grown);
                return grown;
            }
        }

    }

}
