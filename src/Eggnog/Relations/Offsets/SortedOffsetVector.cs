using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An owned, growable, sorted (non-decreasing) vector of row ids in a pooled buffer. Port of egglog's
    /// <c>SortedOffsetVector</c> (core-relations/src/offsets/mod.rs:57-122): the builder for a sparse run, which
    /// <see cref="ToSubset"/> hands to a <see cref="Subset"/> without copying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The buffer is rented lazily from <see cref="OffsetBufferPool"/>; <c>default</c> is an empty vector that owns
    /// nothing. Ownership follows the same rules as <see cref="Subset"/>: move-only by convention, released exactly
    /// once by <see cref="Dispose"/> (or handed off by <see cref="ToSubset"/>), held in a field or local and never
    /// behind a property. It does not implement <see cref="IDisposable"/> for the reason given on
    /// <see cref="Subset"/>.
    /// </para>
    /// <para>
    /// Duplicates are allowed. Appends grow the buffer by doubling, so they are amortized O(1).
    /// </para>
    /// </remarks>
    internal struct SortedOffsetVector
    {

        int[]? items;
        int count;

        /// <summary>
        /// Gets the number of row ids.
        /// </summary>
        internal readonly int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => count;
        }

        /// <summary>
        /// Gets whether the vector holds no row id. Port of <c>Offsets::is_empty</c>.
        /// </summary>
        internal readonly bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => count == 0;
        }

        /// <summary>
        /// Gets the row id at <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The position.</param>
        internal readonly int this[int index]
        {
            get
            {
                if ((uint)index >= (uint)count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                return items![index];
            }
        }

        /// <summary>
        /// Borrows the row ids as a sorted slice. Port of <c>SortedOffsetVector::slice</c>. Valid until the vector is
        /// next changed. O(1).
        /// </summary>
        /// <returns>The slice.</returns>
        internal readonly SortedOffsetSlice Slice() => SortedOffsetSlice.FromTrusted(items ?? Array.Empty<int>(), 0, count);

        /// <summary>
        /// Appends <paramref name="offset"/>, which must be at least the last row id. Port of
        /// <c>SortedOffsetVector::push</c>, which <c>assert!</c>s the order in release builds too. Amortized O(1).
        /// </summary>
        /// <param name="offset">The row id.</param>
        /// <exception cref="InvalidOperationException"><paramref name="offset"/> is smaller than the last row
        /// id.</exception>
        internal void Push(int offset)
        {
            if (count > 0 && items![count - 1] > offset)
                ThrowUnsorted(items[count - 1], offset);

            PushUnchecked(offset);
        }

        /// <summary>
        /// Appends <paramref name="offset"/> without checking the order outside debug builds. Port of
        /// <c>SortedOffsetVector::push_unchecked</c>: the caller guarantees <paramref name="offset"/> is at least the
        /// last row id. Amortized O(1).
        /// </summary>
        /// <param name="offset">The row id.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void PushUnchecked(int offset)
        {
            Debug.Assert(count == 0 || items![count - 1] <= offset, "offsets pushed out of order");
            var buffer = items;
            if (buffer is null || count == buffer.Length)
                buffer = Grow(count + 1);

            buffer[count++] = offset;
        }

        /// <summary>
        /// Keeps the row ids <paramref name="filter"/> accepts, in order. Port of <c>SortedOffsetVector::retain</c>.
        /// O(<see cref="Count"/>).
        /// </summary>
        /// <typeparam name="TFilter">The filter type.</typeparam>
        /// <param name="filter">The filter.</param>
        internal void Retain<TFilter>(ref TFilter filter)
            where TFilter : struct, IOffsetFilter
        {
            if (count > 0)
                count = SortedOffsets.Retain(items!, 0, count, ref filter);
        }

        /// <summary>
        /// Appends every row id of <paramref name="other"/>, whose first row id must be at least the last row id of
        /// this vector. Port of <c>SortedOffsetVector::extend_nonoverlapping</c>. O(<c>other.Length</c>).
        /// </summary>
        /// <param name="other">The slice to append.</param>
        /// <exception cref="InvalidOperationException">The slice starts below the last row id.</exception>
        internal void ExtendNonoverlapping(SortedOffsetSlice other)
        {
            var length = other.Length;
            if (length == 0)
                return;

            if (count > 0 && items![count - 1] > other[0])
                throw new InvalidOperationException("attempting to extend with overlapping offsets");

            var buffer = items;
            if (buffer is null || buffer.Length - count < length)
                buffer = Grow(count + length);

            Array.Copy(other.Array, other.Start, buffer, count, length);
            count += length;
        }

        /// <summary>
        /// Replaces the contents with the row ids of <paramref name="range"/>. Port of
        /// <c>SortedOffsetVector::fill_from_dense</c>. O(<c>range.Size</c>).
        /// </summary>
        /// <param name="range">The range.</param>
        internal void FillFromDense(OffsetRange range)
        {
            count = 0;
            var length = range.Size;
            if (length == 0)
                return;

            var buffer = items;
            if (buffer is null || buffer.Length < length)
                buffer = Grow(length);

            var span = buffer.AsSpan(0, length);
            var first = range.Start;
            for (var i = 0; i < span.Length; i++)
                span[i] = first + i;

            count = length;
        }

        /// <summary>
        /// Removes every row id but keeps the buffer for reuse. Port of <c>Clear::clear</c>.
        /// </summary>
        internal void Clear() => count = 0;

        /// <summary>
        /// Moves the contents into a sparse <see cref="Subset"/> without copying and leaves this vector empty and
        /// owning nothing. Port of wrapping the pooled vector in <c>Subset::Sparse</c>. O(1) (plus a debug-only
        /// order check).
        /// </summary>
        /// <returns>The subset, which now owns the buffer.</returns>
        internal Subset ToSubset()
        {
            var subset = Subset.FromOwnedRun(items ?? Array.Empty<int>(), 0, count);
            items = null;
            count = 0;
            return subset;
        }

        /// <summary>
        /// Returns the buffer to the pool and leaves the vector empty. Harmless to call twice on the same variable.
        /// </summary>
        internal void Dispose()
        {
            if (items is not null)
                OffsetBufferPool.Return(items);

            items = null;
            count = 0;
        }

        /// <summary>
        /// Gets the half-open range enclosing every row id, if any. Port of <c>Offsets::bounds</c>. O(1).
        /// </summary>
        /// <param name="bounds">The enclosing range.</param>
        /// <returns><see langword="false"/> if the vector is empty.</returns>
        internal readonly bool TryGetBounds(out OffsetRange bounds) => Slice().TryGetBounds(out bounds);

        /// <summary>
        /// Calls <paramref name="visitor"/> on every row id in order. Port of <c>Offsets::offsets</c>.
        /// </summary>
        /// <typeparam name="TVisitor">The visitor type.</typeparam>
        /// <param name="visitor">The visitor.</param>
        internal readonly void ForEach<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IOffsetVisitor
        {
            Slice().ForEach(ref visitor);
        }

        /// <summary>
        /// Gets an enumerator over the row ids in order.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public readonly OffsetEnumerator GetEnumerator() => new(items ?? Array.Empty<int>(), 0, count);

        /// <inheritdoc />
        public override readonly string ToString() => Slice().ToString();

        /// <summary>
        /// Replaces the buffer with one of at least <paramref name="minimum"/> slots (and at least twice the old
        /// length), keeping the contents.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        int[] Grow(int minimum)
        {
            var old = items;
            var buffer = OffsetBufferPool.Rent((int)Math.Min(Array.MaxLength, Math.Max(minimum, (old?.Length ?? 0) * 2L)));
            if (old is not null)
            {
                Array.Copy(old, buffer, count);
                OffsetBufferPool.Return(old);
            }

            items = buffer;
            return buffer;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowUnsorted(int last, int offset)
        {
            throw new InvalidOperationException($"Offset {offset} pushed after offset {last}: offsets must be pushed in non-decreasing order.");
        }

    }

}
