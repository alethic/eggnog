using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="IOffsetFilter"/> over a delegate that also records every row id it is asked about, so tests can
    /// check the order and number of calls.
    /// </summary>
    readonly struct PredicateFilter : IOffsetFilter
    {

        readonly Func<int, bool> keep;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="keep">Decides whether a row id stays.</param>
        public PredicateFilter(Func<int, bool> keep)
        {
            this.keep = keep;
            Seen = new List<int>();
        }

        /// <summary>
        /// Gets the row ids the filter was asked about, in order.
        /// </summary>
        public List<int> Seen { get; }

        /// <inheritdoc />
        public bool Keep(int row)
        {
            Seen.Add(row);
            return keep(row);
        }

    }

}
