using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Enumerates the row ids of a dense range or of a sorted run in an array, in ascending order. Returned by the
    /// <c>GetEnumerator</c> methods of <see cref="OffsetRange"/>, <see cref="SortedOffsetSlice"/>,
    /// <see cref="SortedOffsetVector"/>, <see cref="SubsetRef"/> and <see cref="Subset"/>, so <c>foreach</c> over any
    /// of them allocates nothing.
    /// </summary>
    /// <remarks>
    /// Public only so that <c>foreach</c> works over the public subset types; it is created by them, never directly.
    /// A plain (non-ref) struct, so compiled plans can hold one in a local and drive it with
    /// <see cref="MoveNext"/> and <see cref="Current"/>. For a dense range <c>rows</c> is <see langword="null"/>
    /// and the cursor walks row ids; otherwise it walks array positions. The enumerator borrows the array: it is
    /// valid only while the owner does not change or return it. Hot loops should prefer the <c>ForEach</c> visitor
    /// methods, which hoist the dense/sparse test out of the loop.
    /// </remarks>
    public struct OffsetEnumerator
    {

        readonly int[]? rows;
        readonly int end;
        int next;
        int current;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rows">The array holding a sorted run, or <see langword="null"/> for a dense range.</param>
        /// <param name="start">The first row id (dense) or array position (sparse).</param>
        /// <param name="end">One past the last row id (dense) or array position (sparse).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal OffsetEnumerator(int[]? rows, int start, int end)
        {
            this.rows = rows;
            this.end = end;
            next = start;
            current = 0;
        }

        /// <summary>
        /// Gets the row id at the cursor. Valid after <see cref="MoveNext"/> returned <see langword="true"/>.
        /// </summary>
        public readonly int Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => current;
        }

        /// <summary>
        /// Advances to the next row id.
        /// </summary>
        /// <returns><see langword="false"/> once every row id has been produced.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            var p = next;
            if (p >= end)
                return false;

            current = rows is null ? p : rows[p];
            next = p + 1;
            return true;
        }

    }

}
