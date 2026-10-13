using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Collects the seed case's source rows for one lifted table, which are its own rows and the ancestor rows that
    /// mention one of its group roots: each row's keys and output, deduplicated by key in a
    /// <see cref="LiftCandidates"/> arena; with a winner filter, only rows whose id keys name the winner.
    /// </summary>
    /// <remarks>
    /// A bridge function's table row is its keys, then its output, then the timestamp and possibly the subsumption
    /// flag, so the first <c>keys + 1</c> cells are exactly what the lift needs. The winner filter serves the ancestor
    /// rows: an ancestor row that names a group root only in its output stands for no new parent key, because its key
    /// cells are either group roots themselves (and probed as such) or stand only for themselves. Runs as a scan
    /// callback: it only computes. No allocation per row once the arena is warm.
    /// </remarks>
    internal struct LiftKeySink : IRowSink
    {

        readonly LiftCandidates target;
        readonly bool[] idKeys;
        readonly int keyCount;
        uint winner;
        bool filter;

        /// <summary>
        /// Initializes a sink into <paramref name="target"/>, whose entries are <paramref name="keyCount"/> keys and the
        /// output, without a filter.
        /// </summary>
        /// <param name="target">The arena; its width must be <paramref name="keyCount"/> + 1 and its key width
        /// <paramref name="keyCount"/>.</param>
        /// <param name="idKeys">Which key columns hold ids.</param>
        /// <param name="keyCount">The function's key count.</param>
        public LiftKeySink(LiftCandidates target, bool[] idKeys, int keyCount)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(idKeys);
            this.target = target;
            this.idKeys = idKeys;
            this.keyCount = keyCount;
            winner = 0;
            filter = false;
        }

        /// <summary>
        /// Gets the number of rows the sink was offered.
        /// </summary>
        public int Offered { get; private set; }

        /// <summary>
        /// Keeps, from now on, only rows that name <paramref name="id"/> in an id key column (for the ancestor rows
        /// that mention a group root).
        /// </summary>
        /// <param name="id">The group root.</param>
        public void FilterOn(uint id)
        {
            winner = id;
            filter = true;
        }

        /// <summary>
        /// Keeps every row from now on (for the seed case's own rows).
        /// </summary>
        public void ClearFilter() => filter = false;

        /// <inheritdoc />
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            Offered++;
            if (filter && NamesWinner(row) == false)
                return;

            target.Add(row.Slice(0, keyCount + 1));
        }

        /// <summary>
        /// Gets whether an id key cell of <paramref name="row"/> is the winner.
        /// </summary>
        readonly bool NamesWinner(ReadOnlySpan<uint> row)
        {
            for (var i = 0; i < keyCount; i++)
                if (idKeys[i] && row[i] == winner)
                    return true;

            return false;
        }

    }

}
