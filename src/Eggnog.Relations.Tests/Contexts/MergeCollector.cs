using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Collects the (member, canonical) pairs <see cref="Context.ScanMerges{TVisitor}"/> visits, in visiting order, and
    /// optionally runs a callback inside each visit (to test what a visitor may do).
    /// </summary>
    readonly struct MergeCollector : IMergeVisitor
    {

        /// <summary>
        /// Initializes a collector with an empty list.
        /// </summary>
        public MergeCollector()
        {
            Pairs = new List<(uint Member, uint Canonical)>();
        }

        /// <summary>
        /// Initializes a collector that runs <paramref name="onVisit"/> inside each visit.
        /// </summary>
        /// <param name="onVisit">The callback.</param>
        public MergeCollector(Action<uint, uint> onVisit)
            : this()
        {
            OnVisit = onVisit;
        }

        /// <summary>
        /// Gets the pairs visited so far.
        /// </summary>
        public List<(uint Member, uint Canonical)> Pairs { get; }

        /// <summary>
        /// Gets the callback run inside each visit, or <c>null</c>.
        /// </summary>
        public Action<uint, uint>? OnVisit { get; }

        /// <inheritdoc />
        public void Visit(uint member, uint canonical)
        {
            Pairs.Add((member, canonical));
            OnVisit?.Invoke(member, canonical);
        }

    }

}
