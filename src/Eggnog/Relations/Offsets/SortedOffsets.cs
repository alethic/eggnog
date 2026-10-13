using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Kernels over sorted runs of row ids stored in <see cref="int"/> arrays: lower-bound search, galloping search,
    /// in-place intersection and in-place retain. They are the bodies of egglog's <c>SortedOffsetSlice</c> searches
    /// and <c>Subset::intersect</c>/<c>retain</c> (core-relations/src/offsets/mod.rs:157-226, 386-509), exposed over
    /// <c>(array, start, end)</c> so compiled plans and the hash index arena can call them without spans.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A run is <c>array[start..end)</c>, sorted non-decreasing. Duplicates are allowed, as in egglog (a
    /// multi-column column index appends a row once per matching column). All positions taken and returned are
    /// absolute array positions.
    /// </para>
    /// <para>
    /// Each kernel checks its runs against the array bounds once, then reads through
    /// <see cref="MemoryMarshal.GetArrayDataReference{T}(T[])"/> without per-element bounds checks, as egglog does
    /// with <c>get_unchecked</c>. Sortedness is a precondition and is not checked.
    /// </para>
    /// </remarks>
    internal static class SortedOffsets
    {

        /// <summary>
        /// Two sparse runs both at most this long are intersected with the two-pointer merge whatever their ratio
        /// (mod.rs:447).
        /// </summary>
        internal const int SmallIntersectLength = 16;

        /// <summary>
        /// Two sparse runs whose lengths are within this factor of each other are intersected with the two-pointer
        /// merge; beyond it the shorter one drives a galloping search in the longer one (mod.rs:448).
        /// </summary>
        internal const int SimilarLengthRatio = 4;

        /// <summary>
        /// Throws unless <c>[start, end)</c> is a valid run of <paramref name="array"/>.
        /// </summary>
        /// <param name="array">The array.</param>
        /// <param name="start">The first position.</param>
        /// <param name="end">One past the last position.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CheckRun(int[] array, int start, int end)
        {
            if ((uint)start > (uint)end || (uint)end > (uint)array.Length)
                ThrowBadRun(array.Length, start, end);
        }

        /// <summary>
        /// Gets whether <c>array[start..end)</c> is sorted non-decreasing. O(<c>end - start</c>); meant for
        /// <see cref="System.Diagnostics.Debug.Assert(bool)"/>.
        /// </summary>
        /// <param name="array">The array.</param>
        /// <param name="start">The first position.</param>
        /// <param name="end">One past the last position.</param>
        /// <returns><see langword="true"/> if sorted.</returns>
        internal static bool IsSorted(int[] array, int start, int end)
        {
            for (var i = start + 1; i < end; i++)
                if (array[i - 1] > array[i])
                    return false;

            return true;
        }

        /// <summary>
        /// Finds the first position in <c>array[start..end)</c> whose row id is at least <paramref name="target"/>,
        /// or <paramref name="end"/> if there is none. With duplicates this is the first copy of
        /// <paramref name="target"/>. Port of <c>SortedOffsetSlice::binary_search_from</c> (mod.rs:157-173), with a
        /// branch-free binary search in place of <c>slice::binary_search</c> plus a linear walk left over
        /// duplicates. O(log(<c>end - start</c>)).
        /// </summary>
        /// <param name="array">The array.</param>
        /// <param name="start">The first position of the run.</param>
        /// <param name="end">One past the last position of the run.</param>
        /// <param name="target">The row id to look for.</param>
        /// <returns>The lower bound, in <c>[start, end]</c>.</returns>
        internal static int LowerBound(int[] array, int start, int end, int target)
        {
            CheckRun(array, start, end);
            return LowerBoundUnchecked(ref MemoryMarshal.GetArrayDataReference(array), start, end, target);
        }

        /// <summary>
        /// Searches <c>array[from..end)</c> for <paramref name="target"/> by galloping: probe <c>from + 1</c>,
        /// <c>from + 3</c>, <c>from + 7</c>, ... until a probe reaches <paramref name="target"/> or the end, then
        /// binary search the last gap. Port of <c>SortedOffsetSlice::scan_for_offset</c> (mod.rs:175-226).
        /// O(log d), where d is the distance from <paramref name="from"/> to the answer, which makes a sweep of
        /// increasing targets across one run cost O(k log(n / k)) for k targets.
        /// </summary>
        /// <param name="array">The array.</param>
        /// <param name="from">The position to search from; at or past <paramref name="end"/> finds nothing.</param>
        /// <param name="end">One past the last position of the run.</param>
        /// <param name="target">The row id to look for.</param>
        /// <param name="index">
        /// When found, the first position at or after <paramref name="from"/> holding <paramref name="target"/>;
        /// otherwise the position of the first row id greater than <paramref name="target"/>, or
        /// <paramref name="end"/>. Either way the cursor to search from for the next, larger target.
        /// </param>
        /// <returns><see langword="true"/> if <paramref name="target"/> occurs at or after <paramref name="from"/>.</returns>
        internal static bool Gallop(int[] array, int from, int end, int target, out int index)
        {
            if (from < 0 || (uint)end > (uint)array.Length)
                ThrowBadRun(array.Length, from, end);

            return GallopUnchecked(ref MemoryMarshal.GetArrayDataReference(array), from, end, target, out index);
        }

        /// <summary>
        /// Removes from the run <c>cur[curStart..curEnd)</c> every row id not in <c>other[otherStart..otherEnd)</c>,
        /// compacting the survivors to the front of the run. Port of the sparse-sparse arm of
        /// <c>Subset::intersect</c> (mod.rs:443-507), including its strategy choice: the two-pointer merge when both
        /// runs have at most <see cref="SmallIntersectLength"/> row ids or their lengths are within
        /// <see cref="SimilarLengthRatio"/> of each other, otherwise a galloping search driven by the shorter run.
        /// With duplicates the result keeps, per row id, the smaller of its two counts.
        /// </summary>
        /// <param name="cur">The array holding the run to narrow; written in place.</param>
        /// <param name="curStart">The first position of the run to narrow.</param>
        /// <param name="curEnd">One past the last position of the run to narrow.</param>
        /// <param name="other">The array holding the run to intersect with; only read. It must not overlap the run
        /// being narrowed.</param>
        /// <param name="otherStart">The first position of the other run.</param>
        /// <param name="otherEnd">One past the last position of the other run.</param>
        /// <returns>The new end of the narrowed run, which still starts at <paramref name="curStart"/>.</returns>
        internal static int IntersectInPlace(int[] cur, int curStart, int curEnd, int[] other, int otherStart, int otherEnd)
        {
            var curLength = curEnd - curStart;
            var otherLength = otherEnd - otherStart;
            if ((curLength <= SmallIntersectLength && otherLength <= SmallIntersectLength)
                || (curLength / SimilarLengthRatio <= otherLength && otherLength <= (long)curLength * SimilarLengthRatio))
                return IntersectByMerge(cur, curStart, curEnd, other, otherStart, otherEnd);

            if (curLength > otherLength)
                return IntersectByGallopInCur(cur, curStart, curEnd, other, otherStart, otherEnd);

            return IntersectByGallopInOther(cur, curStart, curEnd, other, otherStart, otherEnd);
        }

        /// <summary>
        /// The two-pointer arm of <see cref="IntersectInPlace"/> (mod.rs:450-467): walk the run to narrow, advance a
        /// cursor in the other run past smaller row ids, and keep a row id when the cursor lands on it.
        /// O(<c>|cur| + |other|</c>).
        /// </summary>
        /// <param name="cur">The array holding the run to narrow; written in place.</param>
        /// <param name="curStart">The first position of the run to narrow.</param>
        /// <param name="curEnd">One past the last position of the run to narrow.</param>
        /// <param name="other">The array holding the run to intersect with.</param>
        /// <param name="otherStart">The first position of the other run.</param>
        /// <param name="otherEnd">One past the last position of the other run.</param>
        /// <returns>The new end of the narrowed run.</returns>
        internal static int IntersectByMerge(int[] cur, int curStart, int curEnd, int[] other, int otherStart, int otherEnd)
        {
            CheckRun(cur, curStart, curEnd);
            CheckRun(other, otherStart, otherEnd);
            ref var c = ref MemoryMarshal.GetArrayDataReference(cur);
            ref var o = ref MemoryMarshal.GetArrayDataReference(other);

            var write = curStart;
            var oi = otherStart;
            for (var ci = curStart; ci < curEnd; ci++)
            {
                var target = Unsafe.Add(ref c, ci);
                while (oi < otherEnd && Unsafe.Add(ref o, oi) < target)
                    oi++;

                if (oi == otherEnd)
                    break;

                if (Unsafe.Add(ref o, oi) == target)
                {
                    Unsafe.Add(ref c, write++) = target;
                    oi++;
                }
            }

            return write;
        }

        /// <summary>
        /// The galloping arm of <see cref="IntersectInPlace"/> for a run to narrow that is much longer than the
        /// other (mod.rs:468-491): walk the other run and gallop forward in the run being narrowed, writing each
        /// match to the front. O(<c>|other| log(|cur| / |other|)</c>).
        /// </summary>
        /// <param name="cur">The array holding the run to narrow; written in place.</param>
        /// <param name="curStart">The first position of the run to narrow.</param>
        /// <param name="curEnd">One past the last position of the run to narrow.</param>
        /// <param name="other">The array holding the run to intersect with.</param>
        /// <param name="otherStart">The first position of the other run.</param>
        /// <param name="otherEnd">One past the last position of the other run.</param>
        /// <returns>The new end of the narrowed run.</returns>
        internal static int IntersectByGallopInCur(int[] cur, int curStart, int curEnd, int[] other, int otherStart, int otherEnd)
        {
            CheckRun(cur, curStart, curEnd);
            CheckRun(other, otherStart, otherEnd);
            ref var c = ref MemoryMarshal.GetArrayDataReference(cur);
            ref var o = ref MemoryMarshal.GetArrayDataReference(other);

            // Every write lands at or before the cursor: each match advances the cursor past its own position.
            var write = curStart;
            var ci = curStart;
            for (var oi = otherStart; oi < otherEnd; oi++)
            {
                if (ci >= curEnd)
                    break;

                var target = Unsafe.Add(ref o, oi);
                if (GallopUnchecked(ref c, ci, curEnd, target, out var found))
                {
                    Unsafe.Add(ref c, write++) = target;
                    ci = found + 1;
                }
                else
                {
                    ci = found;
                }
            }

            return write;
        }

        /// <summary>
        /// The galloping arm of <see cref="IntersectInPlace"/> for a run to narrow that is much shorter than the
        /// other (mod.rs:492-506): retain each row id of the run being narrowed that a galloping search finds in the
        /// other run. O(<c>|cur| log(|other| / |cur|)</c>). Unlike egglog's <c>Vec::retain</c>, it stops once the
        /// other run is exhausted, since nothing after that can match.
        /// </summary>
        /// <param name="cur">The array holding the run to narrow; written in place.</param>
        /// <param name="curStart">The first position of the run to narrow.</param>
        /// <param name="curEnd">One past the last position of the run to narrow.</param>
        /// <param name="other">The array holding the run to intersect with.</param>
        /// <param name="otherStart">The first position of the other run.</param>
        /// <param name="otherEnd">One past the last position of the other run.</param>
        /// <returns>The new end of the narrowed run.</returns>
        internal static int IntersectByGallopInOther(int[] cur, int curStart, int curEnd, int[] other, int otherStart, int otherEnd)
        {
            CheckRun(cur, curStart, curEnd);
            CheckRun(other, otherStart, otherEnd);
            ref var c = ref MemoryMarshal.GetArrayDataReference(cur);
            ref var o = ref MemoryMarshal.GetArrayDataReference(other);

            var write = curStart;
            var oi = otherStart;
            for (var ci = curStart; ci < curEnd && oi < otherEnd; ci++)
            {
                var row = Unsafe.Add(ref c, ci);
                if (GallopUnchecked(ref o, oi, otherEnd, row, out var found))
                {
                    Unsafe.Add(ref c, write++) = row;
                    oi = found + 1;
                }
                else
                {
                    oi = found;
                }
            }

            return write;
        }

        /// <summary>
        /// Keeps the row ids of <c>array[start..end)</c> that <paramref name="filter"/> accepts, compacted to the
        /// front of the run in their original order. Port of <c>Vec::retain</c> as egglog uses it for sparse
        /// subsets (mod.rs:75-77, 397). The filter sees each row id exactly once, in order. Branch-free compaction;
        /// O(<c>end - start</c>). If the filter throws, the run is left in an unspecified state.
        /// </summary>
        /// <typeparam name="TFilter">The filter type; a struct, so the loop is specialized per filter.</typeparam>
        /// <param name="array">The array; written in place.</param>
        /// <param name="start">The first position of the run.</param>
        /// <param name="end">One past the last position of the run.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The new end of the run, which still starts at <paramref name="start"/>.</returns>
        internal static int Retain<TFilter>(int[] array, int start, int end, ref TFilter filter)
            where TFilter : struct, IOffsetFilter
        {
            CheckRun(array, start, end);
            ref var data = ref MemoryMarshal.GetArrayDataReference(array);

            // The write position never passes the read position, so overwriting before testing is safe.
            var write = start;
            for (var read = start; read < end; read++)
            {
                var row = Unsafe.Add(ref data, read);
                Unsafe.Add(ref data, write) = row;
                write += filter.Keep(row) ? 1 : 0;
            }

            return write;
        }

        /// <summary>
        /// <see cref="LowerBound"/> without the range check. Branch-free: each step halves the candidate window with
        /// a conditional move, and the window never reads outside <c>[start, end)</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int LowerBoundUnchecked(ref int data, int start, int end, int target)
        {
            var length = end - start;
            if (length <= 0)
                return start;

            // Invariant: the answer lies in [b, b + length].
            var b = start;
            while (length > 1)
            {
                var half = length >> 1;
                b = Unsafe.Add(ref data, b + half) < target ? b + half : b;
                length -= half;
            }

            return b + (Unsafe.Add(ref data, b) < target ? 1 : 0);
        }

        /// <summary>
        /// <see cref="Gallop"/> without the range check. The three O(1) exits of egglog's <c>scan_for_offset</c>
        /// are inlined into the intersection loops; the gallop itself is out of line.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool GallopUnchecked(ref int data, int from, int end, int target, out int index)
        {
            if (from >= end)
            {
                index = end;
                return false;
            }

            var first = Unsafe.Add(ref data, from);
            if (first == target)
            {
                index = from;
                return true;
            }

            if (first > target)
            {
                index = from;
                return false;
            }

            return GallopFrom(ref data, from, end, target, out index);
        }

        /// <summary>
        /// The galloping phase of <see cref="Gallop"/>, entered with <c>data[lo] &lt; target</c>.
        /// </summary>
        static bool GallopFrom(ref int data, int lo, int end, int target, out int index)
        {
            // Probe lo + 1, lo + 3, lo + 7, ... (egglog moves lo to each probe that is still too small and doubles the
            // step). The step stays below end - lo <= int.MaxValue before doubling, so it cannot overflow a uint.
            var step = 1u;
            int hi;
            while (true)
            {
                if (step >= (uint)(end - lo))
                {
                    hi = end;
                    break;
                }

                var probe = lo + (int)step;
                if (Unsafe.Add(ref data, probe) >= target)
                {
                    hi = probe + 1;
                    break;
                }

                lo = probe;
                step <<= 1;
            }

            // Invariant: data[lo] < target, and hi == end or data[hi - 1] >= target. The lower bound of the window is
            // the first copy of target if present (egglog walks left over duplicates to the same position).
            var i = LowerBoundUnchecked(ref data, lo + 1, hi, target);
            index = i;
            return i < end && Unsafe.Add(ref data, i) == target;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowBadRun(int length, int start, int end)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"The run [{start}, {end}) does not fit an array of length {length}.");
        }

    }

}
