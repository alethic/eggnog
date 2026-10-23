using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Compares two tuples of a <see cref="TupleBuffer"/> by their numbers: lexicographically by cells, or, given the
    /// column types, in content order. Sorts a buffer's order without moving its cells.
    /// </summary>
    /// <remarks>
    /// A struct, so a sort boxes it once per call rather than once per compare. In content order, distinct cells that a
    /// type compares as equal fall back to the cells' order, so the order is total and deterministic. Each compare reads
    /// at most every cell of both tuples and decodes the cells it compares by content.
    /// </remarks>
    internal readonly struct TupleOrder : IComparer<int>
    {

        readonly uint[] cells;
        readonly int width;
        readonly IReadOnlyList<ColumnType>? columns;
        readonly Relations.ContainerView view;

        /// <summary>
        /// Initializes an order over the tuples stored row-major in <paramref name="cells"/>.
        /// </summary>
        /// <param name="cells">The cells, <paramref name="width"/> per tuple.</param>
        /// <param name="width">The number of cells per tuple.</param>
        /// <param name="columns">The column types for content order; <c>null</c> to compare cells.</param>
        /// <param name="view">The containers of the scope the cells belong to, through which content order reads a
        /// container's cell.</param>
        public TupleOrder(uint[] cells, int width, IReadOnlyList<ColumnType>? columns, Relations.ContainerView view)
        {
            this.cells = cells;
            this.width = width;
            this.columns = columns;
            this.view = view;
        }

        /// <summary>
        /// Compares the tuples numbered <paramref name="x"/> and <paramref name="y"/>.
        /// </summary>
        /// <param name="x">One tuple's number.</param>
        /// <param name="y">The other tuple's number.</param>
        /// <returns>Negative, zero or positive, as tuple <paramref name="x"/> comes before, with or after tuple
        /// <paramref name="y"/>.</returns>
        public int Compare(int x, int y)
        {
            var a = new ReadOnlySpan<uint>(cells, x * width, width);
            var b = new ReadOnlySpan<uint>(cells, y * width, width);
            if (columns is null)
                return a.SequenceCompareTo(b);

            for (var column = 0; column < width; column++)
            {
                var left = a[column];
                var right = b[column];
                if (left == right)
                    continue;

                var order = columns[column].CompareCells(left, right, view);
                return order != 0 ? order : left.CompareTo(right);
            }

            return 0;
        }

    }

}
