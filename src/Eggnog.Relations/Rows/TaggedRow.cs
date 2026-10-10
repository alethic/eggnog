using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A row of a <see cref="TaggedRowBuffer"/>: the source row id it was tagged with and its cells. egglog's
    /// <c>(RowId, &amp;[Value])</c> pairs. Deconstructs, so <c>foreach (var (id, row) in buffer.EnumerateRows())</c>
    /// works.
    /// </summary>
    public readonly ref struct TaggedRow
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rowId">The source row id.</param>
        /// <param name="values">The row's cells.</param>
        public TaggedRow(int rowId, Span<uint> values)
        {
            RowId = rowId;
            Values = values;
        }

        /// <summary>
        /// Gets the id of the row in the source this row came from.
        /// </summary>
        public int RowId { get; }

        /// <summary>
        /// Gets the row's cells, without the tag. Writable, for in-place rewrites during rebuilds.
        /// </summary>
        public Span<uint> Values { get; }

        /// <summary>
        /// Deconstructs into the source row id and the cells.
        /// </summary>
        /// <param name="rowId">The source row id.</param>
        /// <param name="values">The row's cells.</param>
        public void Deconstruct(out int rowId, out Span<uint> values)
        {
            rowId = RowId;
            values = Values;
        }

    }

}
