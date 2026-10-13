using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Row-level primitives over a table's raw row storage, for compiled kernels: static, non-virtual and over arrays
    /// and offsets, as expression trees need (they cannot use spans or ref structs).
    /// </summary>
    /// <remarks>
    /// The interpreter's scans test visibility with a <see cref="HiddenCursor"/> walk; a compiled per-piece kernel
    /// probes the sealed sets directly through <see cref="IsVisible"/>, which reads only the arrays a
    /// <see cref="HiddenSet"/> publishes. A context's hidden sets are sealed outside its merges, whenever rows are
    /// read, so this test agrees with every other read path.
    /// </remarks>
    internal static class RowOps
    {

        /// <summary>
        /// Gets whether row <paramref name="row"/>, whose first cell is <paramref name="data"/>[<paramref name="rowBase"/>],
        /// is visible: live (its first cell is not <see cref="Value.StaleRep"/>) and in neither hidden set. O(1) plus at
        /// most two sealed probes (a bit test, or a binary search).
        /// </summary>
        /// <param name="data">The layer's row-major storage (<see cref="SortedWritesTable.Rows"/>).</param>
        /// <param name="rowBase">The offset of the row's first cell: <paramref name="row"/> times the arity.</param>
        /// <param name="row">The row id.</param>
        /// <param name="frozen">The frozen part of the view's hidden rows (<see cref="HiddenRows.Frozen"/>), sealed, or
        /// <c>null</c>.</param>
        /// <param name="live">The live part (<see cref="HiddenRows.Live"/>), sealed, or <c>null</c>.</param>
        /// <returns><c>true</c> if the view sees the row.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsVisible(uint[] data, int rowBase, int row, HiddenSet? frozen, HiddenSet? live)
        {
            Debug.Assert(rowBase >= 0 && rowBase < data.Length, "the row lies outside the storage");
            return data[rowBase] != Value.StaleRep
                && (frozen is null || frozen.ContainsSealed(row) == false)
                && (live is null || live.ContainsSealed(row) == false);
        }

    }

}
