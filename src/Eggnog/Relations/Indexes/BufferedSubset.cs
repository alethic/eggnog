using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The rows an index lists under one key: a dense range of row ids, or a sorted run of row ids in the index
    /// shard's <see cref="SubsetBuffer"/>. egglog's <c>BufferedSubset</c> (core-relations/src/hash_index/mod.rs:931-973),
    /// with its <c>BufferedVec</c> (mod.rs:904-929) folded into the sparse arm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Representation: two <see cref="int"/>s, eight bytes. Dense: <c>(start, end)</c> with <c>0 &lt;= start &lt;= end</c>,
    /// holding the row ids <c>[start, end)</c>. Sparse: <c>(~arenaStart, arenaEnd)</c>, so the first field is negative,
    /// holding the row ids at arena positions <c>[arenaStart, arenaEnd)</c>, sorted non-decreasing. <c>default</c> is
    /// the empty dense range. A key whose rows are contiguous (singletons, appended runs) stays dense and costs no arena
    /// space; the first gap moves it to the arena.
    /// </para>
    /// <para>
    /// A sparse run of length <c>L</c> owns an arena block of <c>next_pow2(L)</c> slots, so appending is amortized O(1)
    /// (<see cref="SubsetBuffer.Push"/>). A sparse subset is only meaningful with the arena it came from; copying an
    /// index must copy the arena with it, as egglog's shallow <c>BufferedVec</c> clone requires.
    /// </para>
    /// </remarks>
    internal readonly struct BufferedSubset
    {

        readonly int lo;
        readonly int hi;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        BufferedSubset(int lo, int hi)
        {
            this.lo = lo;
            this.hi = hi;
        }

        /// <summary>
        /// Gets the empty subset, the dense range <c>[0, 0)</c>. egglog's <c>BufferedSubset::empty</c>.
        /// </summary>
        public static BufferedSubset Empty => default;

        /// <summary>
        /// Creates the dense subset holding just <paramref name="row"/>. egglog's <c>BufferedSubset::singleton</c>.
        /// </summary>
        /// <param name="row">The row id.</param>
        /// <returns>The subset.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BufferedSubset Singleton(int row)
        {
            Debug.Assert(row >= 0);
            return new(row, row + 1);
        }

        /// <summary>
        /// Creates the dense subset holding the row ids <c>[start, end)</c>.
        /// </summary>
        /// <param name="start">The first row id.</param>
        /// <param name="end">One past the last row id.</param>
        /// <returns>The subset.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BufferedSubset Dense(int start, int end)
        {
            Debug.Assert(0 <= start && start <= end);
            return new(start, end);
        }

        /// <summary>
        /// Creates the sparse subset holding the row ids at arena positions <c>[start, end)</c>, which the caller has
        /// written in non-decreasing order into a block it owns.
        /// </summary>
        /// <param name="start">The first arena position.</param>
        /// <param name="end">One past the last arena position.</param>
        /// <returns>The subset.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BufferedSubset Sparse(int start, int end)
        {
            Debug.Assert(0 <= start && start < end);
            return new(~start, end);
        }

        /// <summary>
        /// Gets whether the rows live in the arena.
        /// </summary>
        public bool IsSparse
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => lo < 0;
        }

        /// <summary>
        /// Gets the first row id (dense) or the first arena position (sparse).
        /// </summary>
        public int Start
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => lo < 0 ? ~lo : lo;
        }

        /// <summary>
        /// Gets one past the last row id (dense) or one past the last arena position (sparse).
        /// </summary>
        public int End
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => hi;
        }

        /// <summary>
        /// Gets the number of row ids, counting duplicates. O(1).
        /// </summary>
        public int Size
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => hi - Start;
        }

        /// <summary>
        /// Returns this subset with <paramref name="row"/> appended; <paramref name="row"/> must be at least every row
        /// already present. egglog's <c>add_row_sorted</c> (core-relations/src/hash_index/mod.rs:939-957): an empty
        /// range becomes <c>[row, row + 1)</c>, a range ending at <paramref name="row"/> grows by one, and any other row
        /// (a gap, or a repeat of the last row) moves the subset into <paramref name="buffer"/>; a sparse subset appends
        /// in place while its block has room and moves to the next size class when it is full. Amortized O(1); the move
        /// to the arena is O(Size) once.
        /// </summary>
        /// <param name="row">The row id to append.</param>
        /// <param name="buffer">The arena of the shard this subset belongs to.</param>
        /// <returns>The new subset; the old value must not be used afterwards.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BufferedSubset AddRowSorted(int row, ref SubsetBuffer buffer)
        {
            Debug.Assert(row >= 0);
            if (lo >= 0)
            {
                if (hi == lo)
                    return new(row, row + 1);

                if (hi == row)
                    return new(lo, row + 1);

                return buffer.PromoteDense(lo, hi, row);
            }

            return buffer.Push(~lo, hi, row);
        }

        /// <summary>
        /// Borrows the rows. egglog's <c>as_ref</c>. O(1); the result is valid until the index is next modified.
        /// </summary>
        /// <param name="arena">The arena of the shard this subset belongs to (<see cref="SubsetBuffer.Rows"/>).</param>
        /// <returns>The rows.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubsetRef AsRef(int[] arena)
        {
            if (lo >= 0)
                return SubsetRef.FromTrusted(null, lo, hi);

            Debug.Assert(SubsetBuffer.IsFilled(arena, ~lo, hi), "a sparse index subset reads an unwritten arena slot");
            return SubsetRef.FromTrusted(arena, ~lo, hi);
        }

        /// <inheritdoc />
        public override string ToString() => lo >= 0 ? $"Dense[{lo}..{hi})" : $"Sparse@[{~lo}..{hi})";

    }

}
