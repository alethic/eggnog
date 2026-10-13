using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the rows of a <see cref="SortedWritesTable"/> layer that a view sees: not tombstoned and not hidden
    /// (colored-contexts spec §6.3, X20). Used with <see cref="Subset.Retain{TFilter}"/>, which visits rows in ascending
    /// order, so the hidden test is a <see cref="HiddenCursor"/> walk.
    /// </summary>
    internal struct VisibleRowFilter : IOffsetFilter
    {

        readonly uint[] data;
        readonly int arity;
        HiddenCursor hidden;

        /// <summary>
        /// Initializes a filter over <paramref name="table"/>'s current rows. The table must not change while the filter
        /// is in use.
        /// </summary>
        /// <param name="table">The layer.</param>
        /// <param name="hidden">The rows the view hides.</param>
        public VisibleRowFilter(SortedWritesTable table, HiddenRows hidden)
        {
            data = table.Rows.Data;
            arity = table.Arity;
            this.hidden = hidden.Cursor();
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Keep(int row) => data[row * arity] != Value.StaleRep && hidden.IsHidden(row) == false;

    }

}
