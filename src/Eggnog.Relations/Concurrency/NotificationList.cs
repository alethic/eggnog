using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The set of dense identifiers notified since the last <see cref="Reset()"/>, in first-notification order. Any
    /// number of threads may notify at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of egglog's <c>NotificationList</c> (concurrency/src/notification_list.rs). egglog notifies a table on
    /// every staged insert or remove (core-relations action/mod.rs:401-409) and resets the list in
    /// <c>merge_all</c> to learn which of its hundreds of tables need merging.
    /// </para>
    /// <para>
    /// Each identifier owns an <see cref="int"/> flag, stored inline as the Rust stores its <c>AtomicBool</c>
    /// (notification_list.rs:58-66, 92-109). A notify reads the flag first and returns if it is set, so the common case
    /// of re-notifying a notified identifier is read-only and contention-free; otherwise an exchange elects exactly
    /// one first notifier, which appends the identifier to the list under a lock.
    /// </para>
    /// <para>
    /// The Rust keeps the flags in a <c>ConcurrentVec</c> and flips them in place under its read lock. Here the flags
    /// live in segments that never move once allocated (the layout of <see cref="ConcurrentBitSet"/>): segment 0
    /// holds the first 128 flags and segment <c>k &gt; 0</c> the <c>128·2^(k-1)</c> flags starting at
    /// <c>128·2^(k-1)</c>, so the total doubles with each segment like the Rust's power-of-two growth. Growth publishes a
    /// new segment directory under a lock; a thread still holding the old directory reaches the same flags, so no flag
    /// set during growth is lost and the notify path takes no lock.
    /// </para>
    /// <para>
    /// Between resets an identifier is listed at most once, provided <see cref="Reset()"/> does not run concurrently
    /// with <see cref="Notify"/>. As in the Rust, a notify racing a reset may be reported twice or coalesced with the
    /// reset; egglog only resets with exclusive access to its database.
    /// </para>
    /// <para>
    /// The Rust uses relaxed orderings on the flags; this uses acquire loads and release stores, which cost the same on
    /// x64 and almost the same on Arm64, and an interlocked exchange (a full fence) where the Rust swaps.
    /// </para>
    /// <para>
    /// Cost: <see cref="Notify"/> of a notified identifier is an acquire load of the directory, a leading-zero count and
    /// two dependent loads, with no write. The Rust type is a clonable <c>Arc</c> handle; share the reference instead.
    /// Space is O(n) in the largest identifier notified.
    /// </para>
    /// </remarks>
    /// <typeparam name="TKey">The identifier type.</typeparam>
    internal sealed class NotificationList<TKey>
        where TKey : struct, INumericId<TKey>
    {

        /// <summary>
        /// The base-2 logarithm of the number of flags in segment 0: 128, the Rust's default <c>ConcurrentVec</c>
        /// capacity.
        /// </summary>
        const int Log2Base = 7;

        readonly object growLock = new();

        /// <summary>
        /// The identifiers notified since the last reset, in first-notification order. Also the lock that guards
        /// itself.
        /// </summary>
        readonly List<TKey> notified = new();

        /// <summary>
        /// The flag segment directory: nonzero flags are notified. Never empty. Replaced wholesale (under
        /// <see cref="growLock"/>) when it grows; segments are shared between successive directories and never
        /// reallocated.
        /// </summary>
        int[][] segments = [new int[1 << Log2Base]];

        /// <summary>
        /// Notifies <paramref name="item"/>. Wait-free and read-only when it is already notified; the first notifier
        /// since the last reset takes a lock to record it.
        /// </summary>
        /// <param name="item">The identifier. Space is linear in the largest one ever passed.</param>
        /// <exception cref="ArgumentOutOfRangeException">The identifier's index is negative.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Notify(TKey item)
        {
            // The Rust calls resize_with unconditionally; here growth is only entered when the segment is missing. A
            // negative index maps to a segment past any that can exist, so Grow rejects it off the fast path.
            var index = item.Index;
            var segment = SegmentOf((uint)index);
            var directory = Volatile.Read(ref segments);
            if ((uint)segment >= (uint)directory.Length)
                directory = Grow(index);

            ref var flag = ref directory[segment][index - SegmentStart(segment)];
            if (Volatile.Read(ref flag) != 0)
                return;

            if (Interlocked.Exchange(ref flag, 1) != 0)
                return;

            Record(item);
        }

        /// <summary>
        /// Clears every notification and returns the identifiers notified since the last reset, in the order they were
        /// first notified.
        /// </summary>
        /// <returns>The notified identifiers; empty if none.</returns>
        public TKey[] Reset()
        {
            TKey[] taken;
            lock (notified)
            {
                if (notified.Count == 0)
                    return Array.Empty<TKey>();

                taken = notified.ToArray();
                notified.Clear();
            }

            ClearFlags(taken);
            return taken;
        }

        /// <summary>
        /// Clears every notification and appends the identifiers notified since the last reset to
        /// <paramref name="destination"/>, in the order they were first notified. Allocation-free once the buffers
        /// have grown.
        /// </summary>
        /// <param name="destination">Receives the notified identifiers.</param>
        /// <returns>The number of identifiers appended.</returns>
        public int Reset(List<TKey> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            var start = destination.Count;
            lock (notified)
            {
                destination.AddRange(notified);
                notified.Clear();
            }

            var added = destination.Count - start;
            ClearFlags(CollectionsMarshal.AsSpan(destination).Slice(start, added));
            return added;
        }

        /// <summary>
        /// Records the first notification of <paramref name="item"/> since the last reset.
        /// </summary>
        /// <param name="item">The identifier.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void Record(TKey item)
        {
            lock (notified)
                notified.Add(item);
        }

        /// <summary>
        /// Clears the flags of <paramref name="items"/>, which were all notified.
        /// </summary>
        /// <remarks>
        /// Every item was recorded under the list's lock after its flag's segment was published, and the caller took
        /// the items under that lock before reading the directory here, so the directory covers them all.
        /// </remarks>
        /// <param name="items">The identifiers taken from the list.</param>
        void ClearFlags(ReadOnlySpan<TKey> items)
        {
            var directory = Volatile.Read(ref segments);
            foreach (var item in items)
            {
                var index = item.Index;
                var segment = SegmentOf((uint)index);
                Debug.Assert(segment < directory.Length, "A notified identifier has no flag.");
                Volatile.Write(ref directory[segment][index - SegmentStart(segment)], 0);
            }
        }

        /// <summary>
        /// Gets the segment holding flag <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The flag index.</param>
        /// <returns>The segment index: 0 for the first 128 flags, then <c>floor(log2(index / 128)) + 1</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int SegmentOf(uint index) => 32 - BitOperations.LeadingZeroCount(index >> Log2Base);

        /// <summary>
        /// Gets the first flag of segment <paramref name="segment"/>, which is also the number of flags in all segments
        /// before it and, for a segment after the first, its own size.
        /// </summary>
        /// <param name="segment">The segment index, at most 24 (the segment of <see cref="int.MaxValue"/>).</param>
        /// <returns>0 for segment 0, otherwise <c>128·2^(segment-1)</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int SegmentStart(int segment) => (int)((1u << segment >> 1) << Log2Base);

        /// <summary>
        /// Allocates every segment up to the one holding flag <paramref name="index"/> and publishes the new directory.
        /// </summary>
        /// <param name="index">The flag that must exist.</param>
        /// <returns>A directory that contains the flag's segment.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        int[][] Grow(int index)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index), index, "An identifier's index must not be negative.");

            var segment = SegmentOf((uint)index);
            lock (growLock)
            {
                var directory = segments;
                if (segment < directory.Length)
                    return directory;

                // Segment 0 always exists, so every new segment k >= 1 holds SegmentStart(k) flags.
                var grown = new int[segment + 1][];
                Array.Copy(directory, grown, directory.Length);
                for (var k = directory.Length; k <= segment; k++)
                    grown[k] = new int[SegmentStart(k)];

                // Release: the new segments are zeroed before any thread can reach them through the directory.
                Volatile.Write(ref segments, grown);
                return grown;
            }
        }

    }

}
