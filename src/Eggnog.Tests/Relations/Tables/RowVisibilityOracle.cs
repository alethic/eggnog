using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A visibility test over a table's raw row storage and a view's sealed hidden sets, for checking the engine's read
    /// paths: it reads only the row's first cell and <see cref="HiddenSet.ContainsSealed"/>, where the engine's scans
    /// walk a <see cref="HiddenCursor"/>. A context's hidden sets are sealed outside its merges, whenever rows are
    /// read, so this test must agree with every read path.
    /// </summary>
    static class RowVisibilityOracle
    {

        /// <summary>
        /// Gets whether row <paramref name="row"/>, whose first cell is
        /// <paramref name="data"/>[<paramref name="rowBase"/>], is visible: live (its first cell is not
        /// <see cref="Value.StaleRep"/>) and in neither hidden set.
        /// </summary>
        /// <param name="data">The layer's row-major storage (<see cref="SortedWritesTable.Rows"/>).</param>
        /// <param name="rowBase">The offset of the row's first cell: <paramref name="row"/> times the arity.</param>
        /// <param name="row">The row id.</param>
        /// <param name="frozen">The frozen part of the view's hidden rows (<see cref="HiddenRows.Frozen"/>), sealed, or
        /// <c>null</c>.</param>
        /// <param name="live">The live part (<see cref="HiddenRows.Live"/>), sealed, or <c>null</c>.</param>
        /// <returns><c>true</c> if the view sees the row.</returns>
        public static bool IsVisible(uint[] data, int rowBase, int row, HiddenSet? frozen, HiddenSet? live)
        {
            Assert.InRange(rowBase, 0, data.Length - 1);
            return data[rowBase] != Value.StaleRep
                && (frozen is null || frozen.ContainsSealed(row) == false)
                && (live is null || live.ContainsSealed(row) == false);
        }

    }

}
