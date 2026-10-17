using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// Wraps a vector so that many threads can append to it at once, each into its own contiguous range, while other
    /// threads keep reading it. Reads are only blocked while the storage is reallocated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of egglog's <c>ParallelVecWriter</c> (concurrency/src/parallel_writer.rs), which backs core-relations'
    /// <c>ParallelRowBufWriter</c> for parallel inserts into sorted-writes tables. A writer reserves its range with one
    /// interlocked add on the logical length, then copies its batch into the storage past the original length while
    /// holding a read lock on it. If the storage is too small, the writer takes the write lock, which waits for every
    /// read lock (in-flight copies and reader handles alike), and grows it to at least double. Growth copies the whole
    /// old array, so completed writes past the original length survive, as with the Rust's <c>realloc</c>. egglog
    /// reserves for all pending rows up front (core-relations table/mod.rs:679-680), so growth is rare.
    /// </para>
    /// <para>
    /// The vector is a storage array plus a length: <see cref="ReadAccess"/>'s
    /// <see cref="ParallelVecReadAccess{T}.Prefix"/> is the original length, and <see cref="Finish"/> returns the
    /// storage with the logical length. Both <see cref="Finish"/> and <see cref="Take"/> need exclusive access: no
    /// write or read access may be in flight.
    /// </para>
    /// <para>
    /// The storage is always exactly a <c>T[]</c> (never a covariant array of a derived type), so creating a span over
    /// it cannot throw and <see cref="WriteSlice(ReadOnlySpan{T})"/> needs no <c>finally</c> to release its read lock.
    /// </para>
    /// <para>
    /// Cost: a write is one interlocked add, one read lock (see <see cref="ReadOptimizedLock{T}"/>) and a block copy.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The element type.</typeparam>
    internal sealed class ParallelVecWriter<T>
    {

        /// <summary>
        /// The storage. Replaced only under the write lock (growth) or with exclusive access (<see cref="Take"/>).
        /// Always an array of exactly <c>T</c>.
        /// </summary>
        readonly ReadOptimizedLock<T[]> data;

        /// <summary>
        /// The length of the wrapped vector: the prefix readable through <see cref="ReadAccess"/>.
        /// </summary>
        int length;

        /// <summary>
        /// The logical length: <see cref="length"/> plus every range reserved so far. Advanced with
        /// <see cref="Interlocked.Add(ref int, int)"/>, never by a negative amount.
        /// </summary>
        int endLength;

        /// <summary>
        /// Initializes a new instance wrapping all of <paramref name="items"/>.
        /// </summary>
        /// <param name="items">The initial elements; the array becomes the writer's storage.</param>
        public ParallelVecWriter(T[] items) : this(items, items?.Length ?? 0)
        {

        }

        /// <summary>
        /// Initializes a new instance wrapping the first <paramref name="length"/> elements of
        /// <paramref name="buffer"/>; the rest of the array is spare capacity for writes.
        /// </summary>
        /// <param name="buffer">The storage, which the writer takes over. Must be exactly a <c>T[]</c>, not an array
        /// of a type derived from <typeparamref name="T"/>.</param>
        /// <param name="length">The number of elements already in it.</param>
        public ParallelVecWriter(T[] buffer, int length)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, buffer.Length);
            if (buffer.GetType() != typeof(T[]))
                throw new ArgumentException($"The storage must be exactly a {typeof(T).Name}[], not a covariant {buffer.GetType().Name}.", nameof(buffer));

            data = new ReadOptimizedLock<T[]>(buffer);
            this.length = length;
            endLength = length;
        }

        /// <summary>
        /// Takes read access to the vector: the original elements, and through the handle's unchecked accessors any
        /// range whose write has completed. Dispose the handle promptly; it blocks growth.
        /// </summary>
        /// <returns>The read handle.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ParallelVecReadAccess<T> ReadAccess() => new(data.Read(), length);

        /// <summary>
        /// Runs <paramref name="f"/> on the original element at <paramref name="index"/> under read access.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="index">The index; must be below the original length.</param>
        /// <param name="f">The function.</param>
        /// <returns>The result of <paramref name="f"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the original elements (the
        /// Rust panics).</exception>
        public TResult WithIndex<TResult>(int index, Func<T, TResult> f)
        {
            ArgumentNullException.ThrowIfNull(f);
            if ((uint)index >= (uint)length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index {index} is not below the original length {length}.");

            using var access = ReadAccess();
            return f(access.Prefix[index]);
        }

        /// <summary>
        /// Runs <paramref name="f"/> on the original elements in <paramref name="range"/> under read access.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="range">The range; must lie within the original length.</param>
        /// <param name="f">The function.</param>
        /// <returns>The result of <paramref name="f"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="range"/> is not within the original elements
        /// (the Rust panics).</exception>
        public TResult WithSlice<TResult>(Range range, ParallelVecSliceFunc<T, TResult> f)
        {
            ArgumentNullException.ThrowIfNull(f);
            var (offset, count) = range.GetOffsetAndLength(length);
            using var access = ReadAccess();
            return f(access.Prefix.Slice(offset, count));
        }

        /// <summary>
        /// Writes <paramref name="items"/> to a contiguous range of the vector. The cold, general form of the Rust's
        /// <c>write_contents</c>: arrays and lists are copied as spans, any other collection is enumerated through its
        /// interfaces.
        /// </summary>
        /// <remarks>
        /// The Rust takes an <c>ExactSizeIterator</c> and panics after the fact if it yielded a different number of
        /// items than it claimed (parallel_writer.rs:178-195), having already written any surplus past its
        /// reservation, over another writer's range. This port stops at the reserved count and throws as soon as the
        /// collection yields more or fewer items than its <see cref="IReadOnlyCollection{T}.Count"/>; the range stays
        /// reserved and any unwritten slots in it hold default values. A negative count, which a Rust <c>usize</c>
        /// cannot express, is rejected before anything is reserved.
        /// </remarks>
        /// <param name="items">The items; <see cref="IReadOnlyCollection{T}.Count"/> must be exact.</param>
        /// <returns>The index of the first item.</returns>
        /// <exception cref="ArgumentException"><paramref name="items"/> reports a negative count.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="items"/> yielded a different number of items
        /// than its count.</exception>
        public int WriteContents(IReadOnlyCollection<T> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            if (items is T[] array)
                return WriteSlice(array);

            if (items is List<T> list)
                return WriteSlice(CollectionsMarshal.AsSpan(list));

            var expected = items.Count;
            if (expected < 0)
                throw new ArgumentException($"The collection reported a negative count ({expected}).", nameof(items));

            var enumerator = items.GetEnumerator();
            try
            {
                return WriteEnumerated(ref enumerator, expected);
            }
            finally
            {
                enumerator.Dispose();
            }
        }

        /// <summary>
        /// Copies the elements of <paramref name="items"/> to a contiguous range of the vector. The array form of
        /// <c>write_contents</c>; the same as <see cref="WriteSlice(ReadOnlySpan{T})"/>.
        /// </summary>
        /// <param name="items">The items.</param>
        /// <returns>The index of the first item.</returns>
        public int WriteContents(T[] items)
        {
            ArgumentNullException.ThrowIfNull(items);
            return WriteSlice(items);
        }

        /// <summary>
        /// Copies the elements of <paramref name="items"/> to a contiguous range of the vector. The list form of
        /// <c>write_contents</c>, copied as a span. The list must not change during the call.
        /// </summary>
        /// <param name="items">The items.</param>
        /// <returns>The index of the first item.</returns>
        public int WriteContents(List<T> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            return WriteSlice(CollectionsMarshal.AsSpan(items));
        }

        /// <summary>
        /// Writes the <paramref name="count"/> items that <paramref name="items"/> yields to a contiguous range of the
        /// vector. The Rust's <c>write_contents</c> over a monomorphized <c>ExactSizeIterator</c>: the JIT specializes
        /// this method per struct enumerator, so the per-item calls are direct and nothing is allocated.
        /// </summary>
        /// <remarks>
        /// The enumerator is advanced from its current position and is not disposed; the caller owns it. As with
        /// <see cref="WriteContents(IReadOnlyCollection{T})"/>, yielding more or fewer than <paramref name="count"/>
        /// items throws without writing outside the reserved range.
        /// </remarks>
        /// <typeparam name="TEnumerator">The enumerator type.</typeparam>
        /// <param name="items">The enumerator.</param>
        /// <param name="count">The exact number of items it will yield.</param>
        /// <returns>The index of the first item.</returns>
        /// <exception cref="InvalidOperationException"><paramref name="items"/> yielded a different number of items
        /// than <paramref name="count"/>.</exception>
        public int WriteContents<TEnumerator>(ref TEnumerator items, int count)
            where TEnumerator : struct, IEnumerator<T>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            return WriteEnumerated(ref items, count);
        }

        /// <summary>
        /// Copies <paramref name="items"/> to a contiguous range of the vector. The Rust's <c>write_slice</c>, and its
        /// <c>write_cell_slice</c> for the <c>Cell</c>-typed row buffers of core-relations.
        /// </summary>
        /// <param name="items">The items.</param>
        /// <returns>The index of the first item.</returns>
        public int WriteSlice(ReadOnlySpan<T> items)
        {
            var start = ReserveSpace(items.Length);
            var reader = ReadCovering(start + items.Length);

            // Nothing between taking and releasing the read lock can throw: the storage covers the range and is exactly
            // a T[], so the span is valid, and the copy cannot fail. So no finally is needed on this hot path.
            items.CopyTo(new Span<T>(reader.Value, start, items.Length));
            reader.Dispose();
            return start;
        }

        /// <summary>
        /// Returns the vector: the storage array, whose first <paramref name="count"/> elements are the original
        /// elements followed by every written range. Requires exclusive access; the writer is left empty, as after
        /// <see cref="Take"/>, and the Rust's writer is consumed.
        /// </summary>
        /// <param name="count">The vector's length.</param>
        /// <returns>The storage array, which may be longer than <paramref name="count"/>.</returns>
        public T[] Finish(out int count) => Take(out count);

        /// <summary>
        /// Returns the vector like <see cref="Finish"/> and resets the writer to wrap an empty vector. Requires
        /// exclusive access.
        /// </summary>
        /// <param name="count">The vector's length.</param>
        /// <returns>The storage array, which may be longer than <paramref name="count"/>.</returns>
        public T[] Take(out int count)
        {
            ref var buffer = ref data.GetMutableRef();
            var result = buffer;
            count = Volatile.Read(ref endLength);
            Debug.Assert(count <= result.Length, "A reserved range was never backed by storage.");
            buffer = Array.Empty<T>();
            length = 0;
            Volatile.Write(ref endLength, 0);
            return result;
        }

        /// <summary>
        /// Writes the items of <paramref name="items"/> to a newly reserved range of <paramref name="expected"/> slots,
        /// never past it.
        /// </summary>
        /// <typeparam name="TEnumerator">The enumerator type: a struct for the specialized path, or the
        /// interface.</typeparam>
        /// <param name="items">The enumerator.</param>
        /// <param name="expected">The number of items it claims to yield; not negative.</param>
        /// <returns>The index of the first item.</returns>
        int WriteEnumerated<TEnumerator>(ref TEnumerator items, int expected)
            where TEnumerator : IEnumerator<T>
        {
            var start = ReserveSpace(expected);
            var reader = ReadCovering(start + expected);
            var written = 0;
            try
            {
                // The enumerator is caller code that may throw, so the read lock is released in a finally (once per
                // batch, not per item).
                var destination = new Span<T>(reader.Value, start, expected);
                while (items.MoveNext())
                {
                    // Bounded by the reservation: a surplus item is never written.
                    if ((uint)written >= (uint)destination.Length)
                        ThrowCountMismatch();

                    destination[written] = items.Current;
                    written++;
                }
            }
            finally
            {
                reader.Dispose();
            }

            if (written != expected)
                ThrowCountMismatch();

            return start;
        }

        /// <summary>
        /// Reserves <paramref name="count"/> slots at the end of the logical length.
        /// </summary>
        /// <param name="count">The number of slots; not negative, so the logical length never moves backwards.</param>
        /// <returns>The first reserved slot.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int ReserveSpace(int count)
        {
            Debug.Assert(count >= 0, "A reservation must not be negative.");
            var end = Interlocked.Add(ref endLength, count);
            if ((uint)end > (uint)Array.MaxLength)
                ThrowTooLong();

            return end - count;
        }

        /// <summary>
        /// Takes a read lock on storage at least <paramref name="end"/> elements long, growing the storage first if
        /// needed. One read lock when no growth is needed, where the Rust takes two.
        /// </summary>
        /// <param name="end">The required storage length.</param>
        /// <returns>The read lock; its storage covers <paramref name="end"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ReadOptimizedLockReader<T[]> ReadCovering(int end)
        {
            while (true)
            {
                var reader = data.Read();
                if (reader.Value.Length >= end)
                    return reader;

                reader.Dispose();
                Grow(end);
            }
        }

        /// <summary>
        /// Grows the storage to at least <paramref name="end"/> elements and at least double its length, under the
        /// write lock, so no copy into the storage is in flight. The whole old array is copied, including completed
        /// writes past the original length. The new array is exactly a <c>T[]</c>.
        /// </summary>
        /// <param name="end">The required storage length.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow(int end)
        {
            using var writer = data.Lock();
            ref var buffer = ref writer.Value;
            if (buffer.Length < end)
                Array.Resize(ref buffer, (int)Math.Min(Math.Max(end, (long)buffer.Length * 2), Array.MaxLength));
        }

        /// <summary>
        /// Throws for a collection whose count was wrong.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowCountMismatch()
        {
            throw new InvalidOperationException("Passed a collection with an incorrect number of items: it did not yield exactly Count items.");
        }

        /// <summary>
        /// Throws for a logical length beyond the largest array.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowTooLong()
        {
            throw new InvalidOperationException($"A {nameof(ParallelVecWriter<T>)} cannot hold more than {Array.MaxLength} elements.");
        }

    }

}
