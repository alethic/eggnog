using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="IColumnIndexVisitor"/> that copies every entry of a <see cref="ColumnIndex"/>, in visiting order.
    /// </summary>
    readonly struct ColumnIndexCollector : IColumnIndexVisitor
    {

        /// <summary>
        /// Initializes a new instance with empty collections.
        /// </summary>
        public ColumnIndexCollector()
        {
            Entries = new Dictionary<uint, int[]>();
            Order = new List<uint>();
        }

        /// <summary>
        /// Gets the rows of each key.
        /// </summary>
        public Dictionary<uint, int[]> Entries { get; }

        /// <summary>
        /// Gets the keys in visiting order.
        /// </summary>
        public List<uint> Order { get; }

        /// <inheritdoc />
        public void Visit(uint key, SubsetRef rows)
        {
            Entries.Add(key, IndexTestUtil.ToArray(rows));
            Order.Add(key);
        }

    }

}
