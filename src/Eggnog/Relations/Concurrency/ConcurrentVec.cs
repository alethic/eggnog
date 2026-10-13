using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// An append-only vector whose reads never wait for writes. Appends are serialized among themselves; a reader sees
    /// a consistent prefix of everything appended before it looked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of egglog's <c>ConcurrentVec</c> (concurrency/src/concurrent_vec.rs). The published length
    /// (<c>head</c>) is written with release semantics after the slot it covers, and read with acquire semantics
    /// before the storage, so every slot below it is initialized. Appends take a private lock, the Rust's
    /// <c>write_lock</c>.
    /// </para>
    /// <para>
    /// The Rust guards its storage with a <c>ReadOptimizedLock</c> so that a reallocation can wait for readers. Here
    /// the garbage collector provides that grace period instead: growth copies the published prefix into a larger array
    /// and publishes it, while readers holding the old array keep reading valid, identical elements. Reads therefore
    /// take no lock at all. The price is that elements are immutable once published: the Rust allows interior mutation
    /// through a read guard (egglog's <c>NotificationList</c> flips an <c>AtomicBool</c> in place), but a write into a
    /// slot of an array that has just been copied would be lost. Store a reference type when published elements must
    /// carry mutable state, or use storage that never moves, as <see cref="NotificationList{TKey}"/> and
    /// <see cref="ConcurrentBitSet"/> do.
    /// </para>
    /// <para>
    /// The Rust's <c>write_lock</c> is a <c>std::sync::Mutex</c>, which is not reentrant: a <c>push</c> or
    /// <c>resize_with</c> from inside <c>resize_with</c>'s closure deadlocks. A C# monitor is reentrant, and such a
    /// nested append would publish a slot that the outer resize then overwrites, so this port detects it and throws
    /// <see cref="InvalidOperationException"/> instead.
    /// </para>
    /// <para>
    /// Cost: <see cref="Count"/> is one acquire load, <see cref="Read"/> and the indexer two (plus a bounds check);
    /// <see cref="Push"/> is an uncontended lock plus amortized O(1) growth, and an already-satisfied
    /// <see cref="ResizeWith"/> is one acquire load.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The element type.</typeparam>
    internal sealed class ConcurrentVec<T>
    {

        readonly object writeLock = new();

        /// <summary>
        /// The storage. Its length is a power of two; slots at or beyond <see cref="head"/> are unpublished.
        /// </summary>
        T[] items;

        /// <summary>
        /// The number of published elements. Written under <see cref="writeLock"/> with release semantics.
        /// </summary>
        int head;

        /// <summary>
        /// Whether <see cref="ResizeWithSlow"/> is running its factory. Read and written only under
        /// <see cref="writeLock"/>, so only the thread running the factory can ever see it set: a reentrant append.
        /// </summary>
        bool resizing;

        /// <summary>
        /// Initializes a new instance with the default capacity of 128.
        /// </summary>
        public ConcurrentVec() : this(128)
        {

        }

        /// <summary>
        /// Initializes a new instance with room for <paramref name="capacity"/> elements, rounded up to a power of two.
        /// </summary>
        /// <param name="capacity">The initial capacity, at most 2^30 (zero allocates one slot).</param>
        public ConcurrentVec(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 1 << 30);
            items = new T[BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, 1))];
        }

        /// <summary>
        /// Gets the number of published elements. Never decreases.
        /// </summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref head);
        }

        /// <summary>
        /// Gets the published element at <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The index; must be below <see cref="Count"/>.</param>
        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // Length first, then storage: any array current at or after the length's publication covers it.
                var count = Volatile.Read(ref head);
                var array = Volatile.Read(ref items);
                if ((uint)index >= (uint)count)
                    ThrowIndexOutOfRange(index, count);

                return array[index];
            }
        }

        /// <summary>
        /// Gets the published elements. The view is a snapshot: it never changes, and later appends are not in it.
        /// </summary>
        /// <returns>The elements published so far.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<T> Read()
        {
            var count = Volatile.Read(ref head);
            return new ReadOnlySpan<T>(Volatile.Read(ref items), 0, count);
        }

        /// <summary>
        /// Appends <paramref name="item"/>. It is visible to readers once this returns.
        /// </summary>
        /// <param name="item">The element.</param>
        /// <returns>Its index.</returns>
        /// <exception cref="InvalidOperationException">Called from the factory of a <see cref="ResizeWith"/> on this
        /// vector.</exception>
        public int Push(T item)
        {
            lock (writeLock)
            {
                if (resizing)
                    ThrowReentrant();

                var index = head;
                var array = EnsureCapacity(index);
                array[index] = item;
                Volatile.Write(ref head, index + 1);
                return index;
            }
        }

        /// <summary>
        /// Grows the vector to <paramref name="newLength"/> elements, filling every new slot with a fresh result of
        /// <paramref name="factory"/> in index order. Does nothing if the vector is already that long, and in that case
        /// takes no lock.
        /// </summary>
        /// <remarks>
        /// egglog's <c>resize_with</c> (concurrent_vec.rs:58-73) only writes slot <c>new_len - 1</c> when the storage
        /// is already long enough, relying on reallocation to have initialized the slots in between. After a
        /// <c>push</c> has grown the storage with uninitialized slots, that publishes uninitialized memory. This port
        /// fills every slot from the old length to <paramref name="newLength"/>, so <see cref="Push"/> and
        /// <see cref="ResizeWith"/> can be mixed freely and the factory runs exactly once per new element.
        /// </remarks>
        /// <param name="newLength">The length to grow to.</param>
        /// <param name="factory">Creates each new element. It must not append to this vector.</param>
        /// <exception cref="InvalidOperationException"><paramref name="factory"/> tried to append to this vector (the
        /// Rust deadlocks).</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResizeWith(int newLength, Func<T> factory)
        {
            if (newLength <= Volatile.Read(ref head))
                return;

            ResizeWithSlow(newLength, factory);
        }

        /// <summary>
        /// The locked part of <see cref="ResizeWith"/>.
        /// </summary>
        /// <param name="newLength">The length to grow to.</param>
        /// <param name="factory">Creates each new element.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ResizeWithSlow(int newLength, Func<T> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            lock (writeLock)
            {
                if (resizing)
                    ThrowReentrant();

                var start = head;
                if (newLength <= start)
                    return;

                // If the factory throws, the slots it filled stay unpublished and the next resize refills them.
                var array = EnsureCapacity(newLength - 1);
                resizing = true;
                try
                {
                    for (var i = start; i < newLength; i++)
                        array[i] = factory();
                }
                finally
                {
                    resizing = false;
                }

                Volatile.Write(ref head, newLength);
            }
        }

        /// <summary>
        /// Makes sure slot <paramref name="index"/> exists, growing the storage to the next power of two at least
        /// <paramref name="index"/> + 1 (and at least double) if not. Called under <see cref="writeLock"/>.
        /// </summary>
        /// <param name="index">The slot that must exist.</param>
        /// <returns>The current storage.</returns>
        T[] EnsureCapacity(int index)
        {
            var array = items;
            if (index < array.Length)
                return array;

            if (index >= Array.MaxLength)
                throw new InvalidOperationException($"A {nameof(ConcurrentVec<T>)} cannot hold more than {Array.MaxLength} elements.");

            var capacity = (int)Math.Min(Math.Max((long)array.Length * 2, (long)BitOperations.RoundUpToPowerOf2((uint)index + 1)), Array.MaxLength);
            var grown = new T[capacity];
            Array.Copy(array, grown, head);

            // Release: the copied prefix is in place before a reader can pick up the new array.
            Volatile.Write(ref items, grown);
            return grown;
        }

        /// <summary>
        /// Throws for an index outside the published elements.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <param name="count">The published length.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowIndexOutOfRange(int index, int count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index {index} is not below the published length {count}.");
        }

        /// <summary>
        /// Throws for an append made from inside a <see cref="ResizeWith"/> factory.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowReentrant()
        {
            throw new InvalidOperationException($"A {nameof(ConcurrentVec<T>)} cannot be appended to from inside its own {nameof(ResizeWith)} factory.");
        }

    }

}
