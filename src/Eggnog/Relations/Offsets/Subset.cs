using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An owned set of row ids of one table: either a dense range or a sorted run in a pooled buffer. Port of
    /// egglog's <c>Subset</c> (core-relations/src/offsets/mod.rs:333-542), the row set of every trie node, index
    /// probe result and constraint refinement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Representation: <c>(rows, start, end)</c>. When <c>rows</c> is <see langword="null"/> the subset is dense and
    /// holds the row ids <c>[start, end)</c>; otherwise it is sparse and holds <c>rows[start..end)</c>, sorted
    /// non-decreasing, duplicates allowed. <c>default</c> is <see cref="Empty"/>, the dense range <c>[0, 0)</c>. A
    /// dense subset owns nothing, so copying it is free; that is the common case (whole tables, deltas, timestamp
    /// ranges, contiguous index groups), and every operation keeps a subset dense for as long as egglog does.
    /// </para>
    /// <para>
    /// Ownership: a sparse subset owns its buffer, rented from <see cref="OffsetBufferPool"/> (backed by
    /// <see cref="System.Buffers.ArrayPool{T}.Shared"/>), and must be released exactly once with
    /// <see cref="Dispose"/>. Treat a <see cref="Subset"/> as move-only: a struct copy of a sparse subset aliases
    /// the buffer, so after copying keep using only one of the two (<see cref="Take"/> moves explicitly), and use
    /// <see cref="Clone"/> for an independent copy. The mutators work in place, so hold a subset in a field, local or
    /// array element, never behind a property or in a <see langword="readonly"/> field, which would mutate a
    /// defensive copy. For the same reason the type does not implement <see cref="IDisposable"/>: a
    /// <see langword="using"/> variable is read-only, and mutating it would silently act on a copy.
    /// </para>
    /// <para>
    /// Not thread-safe for mutation; any number of threads may read a subset nobody mutates.
    /// </para>
    /// </remarks>
    internal struct Subset : IEquatable<Subset>
    {

        int[]? rows;
        int start;
        int end;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Subset(int[]? rows, int start, int end)
        {
            this.rows = rows;
            this.start = start;
            this.end = end;
        }

        /// <summary>
        /// Gets the empty subset, the dense range <c>[0, 0)</c>. Port of <c>Subset::empty</c>.
        /// </summary>
        public static Subset Empty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default;
        }

        /// <summary>
        /// Creates a dense subset holding the row ids of <paramref name="range"/>. O(1), no allocation.
        /// </summary>
        /// <param name="range">The range.</param>
        /// <returns>The subset.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Subset Dense(OffsetRange range) => new(null, range.Start, range.End);

        /// <summary>
        /// Creates a dense subset holding the row ids <c>[start, end)</c>. O(1), no allocation.
        /// </summary>
        /// <param name="start">The first row id.</param>
        /// <param name="end">One past the last row id.</param>
        /// <returns>The subset.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Subset Dense(int start, int end) => Dense(new OffsetRange(start, end));

        /// <summary>
        /// Creates a sparse subset that takes ownership of the sorted run <c>rows[start..end)</c>. The buffer must
        /// come from <see cref="OffsetBufferPool"/> (or be <see cref="Array.Empty{T}"/>) and nothing else may use it
        /// afterwards. The bounds are checked; the order only in debug builds.
        /// </summary>
        /// <param name="rows">The buffer, whose ownership moves to the subset.</param>
        /// <param name="start">The first position of the run.</param>
        /// <param name="end">One past the last position of the run.</param>
        /// <returns>The subset.</returns>
        internal static Subset FromOwnedRun(int[] rows, int start, int end)
        {
            SortedOffsets.CheckRun(rows, start, end);
            Debug.Assert(SortedOffsets.IsSorted(rows, start, end), "run is not sorted");
            return new(rows, start, end);
        }

        /// <summary>
        /// Gets the number of row ids, counting duplicates. Port of <c>Subset::size</c>. O(1), branch-free.
        /// </summary>
        public readonly int Size
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => end - start;
        }

        /// <summary>
        /// Gets whether the subset is a dense range. Port of <c>Subset::is_dense</c>.
        /// </summary>
        internal readonly bool IsDense
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => rows is null;
        }

        /// <summary>
        /// Gets whether the subset holds no row id. Port of <c>Offsets::is_empty</c>. An empty subset need not equal
        /// <see cref="Empty"/>: intersecting two dense ranges can leave <c>[x, x)</c>, and a sparse subset stays
        /// sparse when emptied. O(1).
        /// </summary>
        internal readonly bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => end == start;
        }

        /// <summary>
        /// Gets the range of a dense subset; the C# form of matching <c>Subset::Dense(range)</c>.
        /// </summary>
        /// <param name="range">The range, when dense.</param>
        /// <returns><see langword="true"/> if the subset is dense.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool TryGetDense(out OffsetRange range)
        {
            if (rows is null)
            {
                range = new OffsetRange(start, end);
                return true;
            }

            range = default;
            return false;
        }

        /// <summary>
        /// Gets the half-open range enclosing every row id: the range itself when dense, <c>[first, last + 1)</c>
        /// when sparse. Port of <c>Offsets::bounds</c>. A dense subset always has bounds, even when empty; a sparse
        /// one has none when empty. O(1).
        /// </summary>
        /// <param name="bounds">The enclosing range.</param>
        /// <returns><see langword="false"/> for an empty sparse subset.</returns>
        internal readonly bool TryGetBounds(out OffsetRange bounds) => AsRef().TryGetBounds(out bounds);

        /// <summary>
        /// Borrows the subset. Port of <c>Subset::as_ref</c>. O(1): a field copy. The result is valid until this
        /// subset is next mutated or disposed.
        /// </summary>
        /// <returns>The borrowed view.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly SubsetRef AsRef() => SubsetRef.FromTrusted(rows, start, end);

        /// <summary>
        /// Keeps only the row ids <paramref name="filter"/> accepts. Port of <c>Subset::retain</c> (mod.rs:386-399).
        /// A sparse subset is compacted in place and stays sparse. A dense subset is rebuilt as if by
        /// <see cref="AddRowSorted"/> over the kept rows: it stays dense while the kept rows are contiguous
        /// (becoming <see cref="Empty"/> if none is kept) and turns sparse at the first gap. The filter sees each
        /// row id once, in ascending order. O(<see cref="Size"/>).
        /// </summary>
        /// <typeparam name="TFilter">The filter type; a struct, so the loop is specialized per filter.</typeparam>
        /// <param name="filter">The filter.</param>
        internal void Retain<TFilter>(ref TFilter filter)
            where TFilter : struct, IOffsetFilter
        {
            if (rows is null)
                RetainDense(ref filter);
            else
                end = SortedOffsets.Retain(rows, start, end, ref filter);
        }

        /// <summary>
        /// Removes every row id not in <paramref name="other"/>. Port of <c>Subset::intersect</c> (mod.rs:400-509):
        /// <list type="bullet">
        /// <item>dense and dense: clamp the range, O(1); an empty result is <c>[x, x)</c>;</item>
        /// <item>dense and sparse: copy the part of <paramref name="other"/> inside the range into a new buffer,
        /// O(log |other| + result); an empty result is <see cref="Empty"/>;</item>
        /// <item>sparse and dense: narrow the run to the range by two binary searches, O(log Size);</item>
        /// <item>sparse and sparse: <see cref="SortedOffsets.IntersectInPlace"/>, a two-pointer merge for similar
        /// sizes and a galloping search driven by the smaller side otherwise.</item>
        /// </list>
        /// A sparse subset stays sparse. Duplicates: against a dense side every copy inside the range is kept;
        /// between two sparse sides each row id keeps the smaller of its two counts.
        /// </summary>
        /// <param name="other">The subset to intersect with. It must not borrow this subset's buffer, except as the
        /// whole of it.</param>
        internal void Intersect(SubsetRef other)
        {
            var otherRows = other.Rows;
            if (rows is null)
            {
                if (otherRows is null)
                {
                    var l = Math.Max(start, other.Lo);
                    var r = Math.Min(end, other.Hi);
                    start = l;
                    end = l >= r ? l : r;
                    return;
                }

                IntersectDenseWithSparse(otherRows, other.Lo, other.Hi);
                return;
            }

            if (otherRows is null)
            {
                // egglog moves the surviving run down to position zero (copy_within, mod.rs:432-442); the window
                // representation narrows it in place instead, so this costs two binary searches and no copy.
                var l = SortedOffsets.LowerBound(rows, start, end, other.Lo);
                end = SortedOffsets.LowerBound(rows, l, end, other.Hi);
                start = l;
                return;
            }

            Debug.Assert(
                ReferenceEquals(rows, otherRows) == false || rows.Length == 0 || (start == other.Lo && end == other.Hi),
                "intersecting a subset with a part of itself");
            end = SortedOffsets.IntersectInPlace(rows, start, end, otherRows, other.Lo, other.Hi);
        }

        /// <summary>
        /// Appends <paramref name="row"/>, which must be at least every row id already present. Port of
        /// <c>Subset::add_row_sorted</c> (mod.rs:511-537). An empty dense subset becomes <c>[row, row + 1)</c>
        /// wherever it was; a dense subset ending at <paramref name="row"/> grows by one; any other row (a gap, or a
        /// repeat of the last row) turns it sparse, renting a buffer and filling it from the range. A sparse subset
        /// appends, growing its buffer by doubling. Amortized O(1); the dense-to-sparse switch is O(Size) once.
        /// </summary>
        /// <param name="row">The row id to append.</param>
        /// <exception cref="InvalidOperationException"><paramref name="row"/> is smaller than the last row id, which
        /// egglog <c>assert!</c>s in release builds too.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddRowSorted(int row)
        {
            Debug.Assert(row >= 0);
            var r = rows;
            if (r is null)
            {
                if (end == start)
                {
                    start = row;
                    end = row + 1;
                    return;
                }

                if (end == row)
                {
                    end = row + 1;
                    return;
                }

                PromoteAndAppend(row);
                return;
            }

            var e = end;
            if (e > start && r[e - 1] > row)
                ThrowUnsorted(r[e - 1], row);

            if (e == r.Length)
            {
                rows = r = OffsetBufferPool.GrowForAppend(r, ref start, ref end);
                e = end;
            }

            r[e] = row;
            end = e + 1;
        }

        /// <summary>
        /// Calls <paramref name="visitor"/> on every row id in ascending order. Port of <c>Offsets::offsets</c>.
        /// O(<see cref="Size"/>), no allocation.
        /// </summary>
        /// <typeparam name="TVisitor">The visitor type.</typeparam>
        /// <param name="visitor">The visitor.</param>
        internal readonly void ForEach<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IOffsetVisitor
        {
            AsRef().ForEach(ref visitor);
        }

        /// <summary>
        /// Gets an enumerator over the row ids in ascending order. It borrows the buffer like <see cref="AsRef"/>.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public readonly OffsetEnumerator GetEnumerator() => new(rows, start, end);

        /// <summary>
        /// Creates an independent copy. Port of <c>Subset::clone</c>: free for a dense subset, a buffer rental and
        /// O(Size) copy for a sparse one.
        /// </summary>
        /// <returns>The copy, which the caller must <see cref="Dispose"/> separately.</returns>
        public readonly Subset Clone()
        {
            if (rows is null)
                return this;

            return new(OffsetBufferPool.RentCopy(rows, start, end), 0, end - start);
        }

        /// <summary>
        /// Moves the subset out, leaving <see cref="Empty"/> behind; the C# form of a Rust move or
        /// <c>mem::take</c>. O(1).
        /// </summary>
        /// <returns>The subset, now owned by the caller.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Subset Take()
        {
            var taken = this;
            this = default;
            return taken;
        }

        /// <summary>
        /// Returns a sparse subset's buffer to the pool and leaves <see cref="Empty"/> behind; does nothing to a
        /// dense one. Calling it again on the same variable is harmless; calling it on a struct copy of a sparse
        /// subset that was already disposed returns the buffer twice and corrupts the pool. Port of dropping a
        /// <c>Pooled&lt;SortedOffsetVector&gt;</c>.
        /// </summary>
        public void Dispose()
        {
            if (rows is not null)
                OffsetBufferPool.Return(rows);

            this = default;
        }

        /// <summary>
        /// Gets whether two subsets are structurally equal, as egglog's derived <c>PartialEq</c>: both dense with the
        /// same <c>start</c> and <c>end</c>, or both sparse with the same row ids. A dense and a sparse subset are
        /// never equal, even with the same rows. O(Size).
        /// </summary>
        /// <param name="other">The other subset.</param>
        /// <returns><see langword="true"/> if equal.</returns>
        public readonly bool Equals(Subset other)
        {
            if (rows is null || other.rows is null)
                return rows is null && other.rows is null && start == other.start && end == other.end;

            return new ReadOnlySpan<int>(rows, start, end - start).SequenceEqual(new ReadOnlySpan<int>(other.rows, other.start, other.end - other.start));
        }

        /// <inheritdoc />
        public readonly override bool Equals([NotNullWhen(true)] object? obj) => obj is Subset other && Equals(other);

        /// <inheritdoc />
        public readonly override int GetHashCode()
        {
            if (rows is null)
                return HashCode.Combine(0, start, end);

            var hash = new HashCode();
            hash.Add(1);
            foreach (var row in new ReadOnlySpan<int>(rows, start, end - start))
                hash.Add(row);

            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public readonly override string ToString() => AsRef().ToString();

        /// <summary>
        /// Gets whether two subsets are structurally equal; see <see cref="Equals(Subset)"/>.
        /// </summary>
        /// <param name="left">The first subset.</param>
        /// <param name="right">The second subset.</param>
        /// <returns><see langword="true"/> if equal.</returns>
        public static bool operator ==(Subset left, Subset right) => left.Equals(right);

        /// <summary>
        /// Gets whether two subsets differ structurally; see <see cref="Equals(Subset)"/>.
        /// </summary>
        /// <param name="left">The first subset.</param>
        /// <param name="right">The second subset.</param>
        /// <returns><see langword="true"/> if not equal.</returns>
        public static bool operator !=(Subset left, Subset right) => left.Equals(right) == false;

        /// <summary>
        /// The dense-and-sparse arm of <see cref="Intersect"/> (mod.rs:413-431).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void IntersectDenseWithSparse(int[] other, int otherStart, int otherEnd)
        {
            if (otherEnd > otherStart)
            {
                var l = SortedOffsets.LowerBound(other, otherStart, otherEnd, start);
                var r = SortedOffsets.LowerBound(other, l, otherEnd, end);

                // egglog checks emptiness before taking a buffer from the pool.
                if (l < r)
                {
                    rows = OffsetBufferPool.RentCopy(other, l, r);
                    start = 0;
                    end = r - l;
                    return;
                }
            }

            this = default;
        }

        /// <summary>
        /// The dense arm of <see cref="Retain"/>: track the kept rows as a range until the first gap, then continue
        /// into a rented buffer.
        /// </summary>
        void RetainDense<TFilter>(ref TFilter filter)
            where TFilter : struct, IOffsetFilter
        {
            var last = end;
            var keptStart = 0;
            var keptEnd = 0;
            var row = start;
            for (; row < last; row++)
            {
                if (filter.Keep(row) == false)
                    continue;

                if (keptEnd == keptStart)
                {
                    keptStart = row;
                    keptEnd = row + 1;
                }
                else if (keptEnd == row)
                {
                    keptEnd = row + 1;
                }
                else
                {
                    break;
                }
            }

            if (row >= last)
            {
                start = keptStart;
                end = keptEnd;
                return;
            }

            // Row is kept and leaves a gap after [keptStart, keptEnd): from here on the result is sparse.
            var count = keptEnd - keptStart;
            var buffer = OffsetBufferPool.Rent(count + 1);
            FillAscending(buffer, count, keptStart);
            buffer[count++] = row;

            var bufferStart = 0;
            for (row++; row < last; row++)
            {
                if (count == buffer.Length)
                    buffer = OffsetBufferPool.GrowForAppend(buffer, ref bufferStart, ref count);

                // Branch-free append: write unconditionally, advance only when kept.
                buffer[count] = row;
                count += filter.Keep(row) ? 1 : 0;
            }

            rows = buffer;
            start = 0;
            end = count;
        }

        /// <summary>
        /// The dense-to-sparse arm of <see cref="AddRowSorted"/>: <c>fill_from_dense</c> followed by <c>push</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void PromoteAndAppend(int row)
        {
            if (row < end - 1)
                ThrowUnsorted(end - 1, row);

            var count = end - start;
            var buffer = OffsetBufferPool.Rent(count + 1);
            FillAscending(buffer, count, start);
            buffer[count] = row;
            rows = buffer;
            start = 0;
            end = count + 1;
        }

        /// <summary>
        /// Writes <c>first, first + 1, ...</c> to the first <paramref name="count"/> positions of
        /// <paramref name="buffer"/>.
        /// </summary>
        static void FillAscending(int[] buffer, int count, int first)
        {
            var span = buffer.AsSpan(0, count);
            for (var i = 0; i < span.Length; i++)
                span[i] = first + i;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowUnsorted(int last, int row)
        {
            throw new InvalidOperationException($"Row {row} added after row {last}: rows must be added in non-decreasing order.");
        }

    }

}
