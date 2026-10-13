using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="ITupleIndexVisitor"/> that copies every entry of a <see cref="TupleIndex"/>, keyed by
    /// <see cref="IndexTestUtil.KeyOf"/>, and records the visiting order.
    /// </summary>
    readonly struct TupleIndexCollector : ITupleIndexVisitor
    {

        /// <summary>
        /// Initializes a new instance with empty collections.
        /// </summary>
        public TupleIndexCollector()
        {
            Entries = new Dictionary<string, int[]>();
            Order = new List<string>();
        }

        /// <summary>
        /// Gets the rows of each key.
        /// </summary>
        public Dictionary<string, int[]> Entries { get; }

        /// <summary>
        /// Gets the keys in visiting order.
        /// </summary>
        public List<string> Order { get; }

        /// <inheritdoc />
        public void Visit(ReadOnlySpan<uint> key, SubsetRef rows)
        {
            var text = IndexTestUtil.KeyOf(key);
            Entries.Add(text, IndexTestUtil.ToArray(rows));
            Order.Add(text);
        }

    }

}
