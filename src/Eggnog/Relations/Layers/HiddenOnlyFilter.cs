using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the rows a view does not hide, for tables without tombstones (a union-find table):
    /// <see cref="VisibleRowFilter"/> without the liveness test. Used with <see cref="Subset.Retain{TFilter}"/>.
    /// </summary>
    internal struct HiddenOnlyFilter : IOffsetFilter
    {

        HiddenCursor hidden;

        /// <summary>
        /// Initializes a filter over <paramref name="hidden"/>.
        /// </summary>
        /// <param name="hidden">The rows the view hides.</param>
        public HiddenOnlyFilter(HiddenRows hidden)
        {
            this.hidden = hidden.Cursor();
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Keep(int row) => hidden.IsHidden(row) == false;

    }

}
