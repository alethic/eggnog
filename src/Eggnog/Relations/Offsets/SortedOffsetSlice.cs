using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// A borrowed, sorted (non-decreasing) run of row ids: <c>Array[Start..End)</c>. Port of egglog's
    /// <c>SortedOffsetSlice</c> (core-relations/src/offsets/mod.rs:124-253), with the slice reference replaced by
    /// an <c>(array, start, end)</c> triple so it can live in fields, closures and compiled expression trees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The slice does not own the array. It is valid while the owner (a <see cref="Subset"/>, a
    /// <see cref="SortedOffsetVector"/>, an index arena) neither changes nor returns it. Copying is free.
    /// </para>
    /// <para>
    /// Positions taken and returned by members (<see cref="this[int]"/>, <see cref="Subslice"/>,
    /// <see cref="BinarySearchById"/>, <see cref="ScanForOffset"/>) are relative to <see cref="Start"/>, as with
    /// a Rust slice. Duplicates are allowed; searches find the first copy.
    /// </para>
    /// </remarks>
    internal readonly struct SortedOffsetSlice
    {

        readonly int[] array;
        readonly int start;
        readonly int end;

        /// <summary>
        /// Initializes a new instance over <c>array[start..end)</c>, which must be sorted. Port of
        /// <c>SortedOffsetSlice::new_unchecked</c>: the bounds are always checked, the order only in debug builds,
        /// in O(<c>end - start</c>).
        /// </summary>
        /// <param name="array">The array holding the run.</param>
        /// <param name="start">The first position of the run.</param>
        /// <param name="end">One past the last position of the run.</param>
        internal SortedOffsetSlice(int[] array, int start, int end)
        {
            SortedOffsets.CheckRun(array, start, end);
            Debug.Assert(SortedOffsets.IsSorted(array, start, end), "slice is not sorted");
            this.array = array;
            this.start = start;
            this.end = end;
        }

        /// <summary>
        /// Gets an empty slice.
        /// </summary>
        internal static SortedOffsetSlice Empty => new(System.Array.Empty<int>(), 0, 0, true);

        /// <summary>
        /// Creates a slice over <c>array[start..end)</c> without checking it: the caller guarantees the run is in
        /// bounds and sorted, typically because it was cut from a <see cref="Subset"/> or another slice. O(1).
        /// </summary>
        /// <param name="array">The array holding the run.</param>
        /// <param name="start">The first position of the run.</param>
        /// <param name="end">One past the last position of the run.</param>
        /// <returns>The slice.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static SortedOffsetSlice FromTrusted(int[] array, int start, int end) => new(array, start, end, true);

        /// <summary>
        /// Gets the array holding the run. Together with <see cref="Start"/> and <see cref="End"/> this is the
        /// <c>(array, offset)</c> form hot paths take; egglog's <c>inner()</c>.
        /// </summary>
        internal int[] Array
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => array ?? System.Array.Empty<int>();
        }

        /// <summary>
        /// Gets the array position of the first row id.
        /// </summary>
        internal int Start
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => start;
        }

        /// <summary>
        /// Gets the array position one past the last row id.
        /// </summary>
        internal int End
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => end;
        }

        /// <summary>
        /// Gets the number of row ids.
        /// </summary>
        internal int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => end - start;
        }

        /// <summary>
        /// Gets whether the slice holds no row id. Port of <c>Offsets::is_empty</c>.
        /// </summary>
        internal bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => end == start;
        }

        /// <summary>
        /// Gets the row id at <paramref name="index"/>, relative to <see cref="Start"/>.
        /// </summary>
        /// <param name="index">The position within the slice.</param>
        internal int this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((uint)index >= (uint)(end - start))
                    throw new ArgumentOutOfRangeException(nameof(index));

                return array[start + index];
            }
        }

        /// <summary>
        /// Gets the row ids as a span, for callers that are not compiled expression trees.
        /// </summary>
        /// <returns>The span.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<int> AsSpan() => new(array, start, end - start);

        /// <summary>
        /// Gets the sub-run at positions <c>[lo, hi)</c> relative to <see cref="Start"/>. Port of
        /// <c>SortedOffsetSlice::subslice</c>; a sub-run of a sorted run is sorted, so nothing is re-checked. O(1).
        /// </summary>
        /// <param name="lo">The first position, relative to this slice.</param>
        /// <param name="hi">One past the last position, relative to this slice.</param>
        /// <returns>The sub-run.</returns>
        internal SortedOffsetSlice Subslice(int lo, int hi)
        {
            if ((uint)lo > (uint)hi || (uint)hi > (uint)(end - start))
                throw new ArgumentOutOfRangeException(nameof(lo), $"[{lo}, {hi}) is not within a slice of length {end - start}.");

            return new SortedOffsetSlice(Array, start + lo, start + hi, true);
        }

        /// <summary>
        /// Finds the position (relative to <see cref="Start"/>) of the first row id that is at least
        /// <paramref name="target"/>, or <see cref="Length"/> if there is none. Port of
        /// <c>SortedOffsetSlice::binary_search_by_id</c>. O(log <see cref="Length"/>).
        /// </summary>
        /// <param name="target">The row id to look for.</param>
        /// <returns>The lower bound, in <c>[0, Length]</c>.</returns>
        internal int BinarySearchById(int target) => SortedOffsets.LowerBound(Array, start, end, target) - start;

        /// <summary>
        /// Gallops forward from position <paramref name="from"/> (relative to <see cref="Start"/>) looking for
        /// <paramref name="target"/>. Port of <c>SortedOffsetSlice::scan_for_offset</c>, returning its
        /// <c>Result&lt;usize, usize&gt;</c> as a flag plus position. O(log d) for a distance d.
        /// </summary>
        /// <param name="from">The position to search from.</param>
        /// <param name="target">The row id to look for.</param>
        /// <param name="index">
        /// When found, the first position at or after <paramref name="from"/> holding <paramref name="target"/>;
        /// otherwise the position of the first larger row id, or <see cref="Length"/>. Always in
        /// <c>[from, Length]</c> when <paramref name="from"/> is at most <see cref="Length"/>.
        /// </param>
        /// <returns><see langword="true"/> if found.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="from"/> is negative.</exception>
        internal bool ScanForOffset(int from, int target, out int index)
        {
            // egglog's cursor is a usize, so it can never point before the slice; a negative int would make the
            // kernel search the owner's array before Start. One check per call, not per probe.
            if (from < 0)
                throw new ArgumentOutOfRangeException(nameof(from), $"Position {from} is negative.");

            if (from >= end - start)
            {
                index = end - start;
                return false;
            }

            var found = SortedOffsets.Gallop(Array, start + from, end, target, out var absolute);
            index = absolute - start;
            return found;
        }

        /// <summary>
        /// Gets the half-open range enclosing every row id, <c>[first, last + 1)</c>, if the slice is nonempty. Port
        /// of <c>Offsets::bounds</c>. O(1).
        /// </summary>
        /// <param name="bounds">The enclosing range.</param>
        /// <returns><see langword="false"/> if the slice is empty.</returns>
        internal bool TryGetBounds(out OffsetRange bounds)
        {
            if (end == start)
            {
                bounds = default;
                return false;
            }

            bounds = new OffsetRange(array[start], array[end - 1] + 1);
            return true;
        }

        /// <summary>
        /// Calls <paramref name="visitor"/> on every row id in order. Port of <c>Offsets::offsets</c>.
        /// O(<see cref="Length"/>), no allocation.
        /// </summary>
        /// <typeparam name="TVisitor">The visitor type.</typeparam>
        /// <param name="visitor">The visitor.</param>
        internal void ForEach<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IOffsetVisitor
        {
            foreach (var row in AsSpan())
                visitor.Visit(row);
        }

        /// <summary>
        /// Gets an enumerator over the row ids in order. Port of <c>SortedOffsetSlice::iter</c>.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public OffsetEnumerator GetEnumerator() => new(Array, start, end);

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder("[");
            for (var i = start; i < end; i++)
            {
                if (i > start)
                    b.Append(", ");

                b.Append(array[i]);
            }

            return b.Append(']').ToString();
        }

        /// <summary>
        /// Initializes a new instance over a run already known to be in bounds and sorted; the flag only selects
        /// this overload.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        SortedOffsetSlice(int[] array, int start, int end, bool trusted)
        {
            Debug.Assert(trusted && (uint)start <= (uint)end && (uint)end <= (uint)array.Length);
            this.array = array;
            this.start = start;
            this.end = end;
        }

    }

}
