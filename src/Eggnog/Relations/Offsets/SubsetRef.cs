using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A borrowed set of row ids: either a dense range or a sorted run in someone else's array. Port of egglog's
    /// <c>SubsetRef</c> (core-relations/src/offsets/mod.rs:255-331), the form in which subsets are passed to scans,
    /// index probes and intersections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Representation: <c>(rows, lo, hi)</c>. When <c>rows</c> is <see langword="null"/> the subset is dense and
    /// holds the row ids <c>[lo, hi)</c>; otherwise it is sparse and holds <c>rows[lo..hi)</c>, sorted
    /// non-decreasing (duplicates allowed). One shape for both kinds makes <see cref="Size"/> and
    /// <see cref="IsEmpty"/> branch-free and <see cref="Subset.AsRef"/> a field copy. <c>default</c> is the empty
    /// dense range <c>[0, 0)</c>.
    /// </para>
    /// <para>
    /// A plain struct (not a ref struct) of 16 bytes, so it fits in fields and compiled expression trees. It does not
    /// own its array: it is valid while the owner (a <see cref="Subset"/>, an index arena, a scratch buffer)
    /// neither changes nor returns it. Turn it into an owned <see cref="Subset"/> with <see cref="ToOwned"/>.
    /// </para>
    /// <para>
    /// Positions (<see cref="IterBounded"/>) count row ids from the start of the subset, not row ids themselves:
    /// position <c>p</c> is row <c>lo + p</c> when dense and <c>rows[lo + p]</c> when sparse.
    /// </para>
    /// </remarks>
    internal readonly struct SubsetRef
    {

        readonly int[]? rows;
        readonly int lo;
        readonly int hi;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        SubsetRef(int[]? rows, int lo, int hi)
        {
            this.rows = rows;
            this.lo = lo;
            this.hi = hi;
        }

        /// <summary>
        /// Creates a dense subset holding the row ids of <paramref name="range"/>. O(1).
        /// </summary>
        /// <param name="range">The range.</param>
        /// <returns>The subset.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SubsetRef Dense(OffsetRange range) => new(null, range.Start, range.End);

        /// <summary>
        /// Creates a sparse subset over a sorted slice. O(1).
        /// </summary>
        /// <param name="slice">The slice.</param>
        /// <returns>The subset.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static SubsetRef Sparse(SortedOffsetSlice slice) => new(slice.Array, slice.Start, slice.End);

        /// <summary>
        /// Creates a sparse subset over the sorted run <c>rows[start..end)</c>. The bounds are checked; the order
        /// only in debug builds, in O(<c>end - start</c>).
        /// </summary>
        /// <param name="rows">The array holding the run.</param>
        /// <param name="start">The first position of the run.</param>
        /// <param name="end">One past the last position of the run.</param>
        /// <returns>The subset.</returns>
        internal static SubsetRef Sparse(int[] rows, int start, int end) => Sparse(new SortedOffsetSlice(rows, start, end));

        /// <summary>
        /// Creates a subset over the sorted run <c>rows[start..end)</c>, returning a run of exactly one row id as a
        /// dense range so that <see cref="ToOwned"/> need not rent a buffer for it. Port of <c>sparse_subset_ref</c>
        /// (core-relations/src/free_join/execute.rs:57-73), used by the small column index of the join.
        /// </summary>
        /// <param name="rows">The array holding the run.</param>
        /// <param name="start">The first position of the run.</param>
        /// <param name="end">One past the last position of the run.</param>
        /// <returns>The subset.</returns>
        internal static SubsetRef FromSortedRun(int[] rows, int start, int end)
        {
            if (end - start == 1)
            {
                var row = rows[start];
                return new(null, row, row + 1);
            }

            return Sparse(rows, start, end);
        }

        /// <summary>
        /// Creates a subset from its raw representation without any check. The caller guarantees that a sparse run
        /// is in bounds and sorted, typically because it comes from a <see cref="Subset"/>. O(1).
        /// </summary>
        /// <param name="rows">The array holding a sorted run, or <see langword="null"/> for a dense range.</param>
        /// <param name="lo">The first row id (dense) or array position (sparse).</param>
        /// <param name="hi">One past the last row id (dense) or array position (sparse).</param>
        /// <returns>The subset.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static SubsetRef FromTrusted(int[]? rows, int lo, int hi)
        {
            Debug.Assert(lo <= hi && (rows is null || (uint)hi <= (uint)rows.Length));
            return new(rows, lo, hi);
        }

        /// <summary>
        /// Gets the array holding the run of a sparse subset, or <see langword="null"/> for a dense one.
        /// </summary>
        internal int[]? Rows
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => rows;
        }

        /// <summary>
        /// Gets the first row id (dense) or the array position of the first row id (sparse).
        /// </summary>
        internal int Lo
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => lo;
        }

        /// <summary>
        /// Gets one past the last row id (dense) or one past the array position of the last row id (sparse).
        /// </summary>
        internal int Hi
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => hi;
        }

        /// <summary>
        /// Gets the number of row ids, counting duplicates. Port of <c>SubsetRef::size</c>. O(1).
        /// </summary>
        internal int Size
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => hi - lo;
        }

        /// <summary>
        /// Gets whether the subset is a dense range.
        /// </summary>
        internal bool IsDense
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => rows is null;
        }

        /// <summary>
        /// Gets whether the subset holds no row id. Port of <c>Offsets::is_empty</c>. O(1).
        /// </summary>
        internal bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => hi == lo;
        }

        /// <summary>
        /// Gets the range of a dense subset; the C# form of matching <c>SubsetRef::Dense(range)</c>.
        /// </summary>
        /// <param name="range">The range, when dense.</param>
        /// <returns><see langword="true"/> if the subset is dense.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetDense(out OffsetRange range)
        {
            if (rows is null)
            {
                range = new OffsetRange(lo, hi);
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
        internal bool TryGetBounds(out OffsetRange bounds)
        {
            if (rows is null)
            {
                bounds = new OffsetRange(lo, hi);
                return true;
            }

            if (hi == lo)
            {
                bounds = default;
                return false;
            }

            bounds = new OffsetRange(rows[lo], rows[hi - 1] + 1);
            return true;
        }

        /// <summary>
        /// Gets the sorted run of a sparse subset. Port of <c>SubsetRef::_slice</c>, which egglog keeps for
        /// debugging.
        /// </summary>
        /// <returns>The slice.</returns>
        /// <exception cref="InvalidOperationException">The subset is dense.</exception>
        internal SortedOffsetSlice AsSlice()
        {
            if (rows is null)
                throw new InvalidOperationException("Getting a slice from a dense subset.");

            return SortedOffsetSlice.FromTrusted(rows, lo, hi);
        }

        /// <summary>
        /// Copies the subset into an owned <see cref="Subset"/>. Port of <c>SubsetRef::to_owned</c>: a dense subset
        /// stays dense and costs nothing; a sparse one rents a buffer from <see cref="OffsetBufferPool"/> and copies
        /// its run (O(<see cref="Size"/>)). An empty sparse subset becomes an empty sparse <see cref="Subset"/>
        /// without renting.
        /// </summary>
        /// <returns>The owned subset; the caller must <see cref="Subset.Dispose"/> it.</returns>
        internal Subset ToOwned()
        {
            if (rows is null)
                return Subset.Dense(new OffsetRange(lo, hi));

            return Subset.FromOwnedRun(OffsetBufferPool.RentCopy(rows, lo, hi), 0, hi - lo);
        }

        /// <summary>
        /// Visits the row ids at positions <c>[from, to)</c>, clipped to the subset, and returns the position to
        /// resume from. Port of <c>SubsetRef::iter_bounded</c>, which tables use for chunked scans. O(chunk).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Dense: visits positions <c>from</c> up to <c>min(to, Size)</c> and returns the position after the last
        /// one visited (or <paramref name="from"/> if none was), unless that reaches <see cref="Size"/>.
        /// </para>
        /// <para>
        /// Sparse: visits positions <c>from</c> up to <c>min(to, Size)</c> and returns that end unless it equals
        /// <see cref="Size"/>. As in egglog, <paramref name="from"/> must not exceed the clipped end; unlike the
        /// dense case, which tolerates it, a sparse subset throws.
        /// </para>
        /// </remarks>
        /// <typeparam name="TVisitor">The visitor type.</typeparam>
        /// <param name="from">The first position to visit; must not be negative.</param>
        /// <param name="to">One past the last position to visit; must not be negative, may exceed <see cref="Size"/>.</param>
        /// <param name="visitor">The visitor.</param>
        /// <returns>The next position to scan from, or <c>-1</c> once the subset is exhausted (egglog's
        /// <c>None</c>). Stop on <c>-1</c>: it is not a valid start position and is rejected if passed back.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="from"/> or <paramref name="to"/> is negative, or, for a sparse subset, <paramref name="from"/>
        /// is past the clipped end.
        /// </exception>
        internal int IterBounded<TVisitor>(int from, int to, ref TVisitor visitor)
            where TVisitor : struct, IOffsetVisitor
        {
            // egglog takes usize positions and returns Option<usize>, so its None can never come back as a start.
            // Here None is -1, so reject negative positions explicitly: in a dense subset they would visit rows
            // below Lo, in a sparse one array slots before the window (another key's rows in an index arena). One
            // check per chunk, not per row.
            if ((from | to) < 0)
                throw new ArgumentOutOfRangeException(from < 0 ? nameof(from) : nameof(to), $"Positions [{from}, {to}) must not be negative; -1 (None) is not a start position.");

            var size = hi - lo;
            var stop = to < size ? to : size;
            if (rows is null)
            {
                if (from >= size)
                    return -1;

                var first = lo + from;
                var last = lo + stop;
                for (var row = first; row < last; row++)
                    visitor.Visit(row);

                var cur = stop > from ? stop : from;
                return cur < size ? cur : -1;
            }

            if (from > stop)
                throw new ArgumentOutOfRangeException(nameof(from), $"Position {from} is past the end of the chunk {stop}.");

            foreach (var row in new ReadOnlySpan<int>(rows, lo + from, stop - from))
                visitor.Visit(row);

            return stop == size ? -1 : stop;
        }

        /// <summary>
        /// Intersects the subset with a dense range without allocating: a dense subset is clamped, a sparse one is
        /// cut to the sub-run inside the range by two binary searches. Port of <c>intersect_with_dense_ref</c>
        /// (core-relations/src/free_join/execute.rs:211-238), which the join applies to every subset it reads from a
        /// whole-table index when the atom covers only part of the table. O(1) dense, O(log Size) sparse.
        /// </summary>
        /// <param name="range">The range to intersect with.</param>
        /// <param name="result">The intersection, borrowing this subset's array, when nonempty.</param>
        /// <returns><see langword="false"/> if the intersection is empty.</returns>
        internal bool TryIntersectDense(OffsetRange range, out SubsetRef result)
        {
            if (rows is null)
            {
                var l = Math.Max(lo, range.Start);
                var r = Math.Min(hi, range.End);
                if (l >= r)
                {
                    result = default;
                    return false;
                }

                result = new SubsetRef(null, l, r);
                return true;
            }

            var ls = SortedOffsets.LowerBound(rows, lo, hi, range.Start);
            var rs = SortedOffsets.LowerBound(rows, ls, hi, range.End);
            if (ls >= rs)
            {
                result = default;
                return false;
            }

            result = new SubsetRef(rows, ls, rs);
            return true;
        }

        /// <summary>
        /// Calls <paramref name="visitor"/> on every row id in ascending order. Port of <c>Offsets::offsets</c>.
        /// O(<see cref="Size"/>), no allocation.
        /// </summary>
        /// <typeparam name="TVisitor">The visitor type.</typeparam>
        /// <param name="visitor">The visitor.</param>
        internal void ForEach<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IOffsetVisitor
        {
            if (rows is null)
            {
                var end = hi;
                for (var row = lo; row < end; row++)
                    visitor.Visit(row);
            }
            else
            {
                foreach (var row in new ReadOnlySpan<int>(rows, lo, hi - lo))
                    visitor.Visit(row);
            }
        }

        /// <summary>
        /// Gets an enumerator over the row ids in ascending order.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public OffsetEnumerator GetEnumerator() => new(rows, lo, hi);

        /// <inheritdoc />
        public override string ToString() => rows is null ? $"Dense[{lo}..{hi})" : "Sparse" + SortedOffsetSlice.FromTrusted(rows, lo, hi);

    }

}
