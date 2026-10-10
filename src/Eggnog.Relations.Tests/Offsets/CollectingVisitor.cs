using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="IOffsetVisitor"/> that records every row id it is given, in order.
    /// </summary>
    readonly struct CollectingVisitor : IOffsetVisitor
    {

        /// <summary>
        /// Initializes a new instance with an empty list.
        /// </summary>
        public CollectingVisitor()
        {
            Rows = new List<int>();
        }

        /// <summary>
        /// Gets the row ids visited so far.
        /// </summary>
        public List<int> Rows { get; }

        /// <inheritdoc />
        public void Visit(int row) => Rows.Add(row);

    }

}
