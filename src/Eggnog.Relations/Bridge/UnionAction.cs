using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A handle for unioning two e-class ids through an <see cref="ExecutionState"/>, holding only ids: egglog-bridge's
    /// <c>UnionAction</c> (egglog-bridge/src/lib.rs:1373-1395). It stages <c>[x, y, timestamp]</c> into the union-find
    /// table; the union takes effect at the next merge, and the rebuild that follows makes the tables canonical.
    /// </summary>
    /// <remarks>
    /// Immutable; usable from any thread with its own state. Ids are used as given.
    /// </remarks>
    internal readonly record struct UnionAction
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="unionFind">The union-find table.</param>
        /// <param name="timestamp">The e-graph's timestamp counter.</param>
        public UnionAction(TableId unionFind, CounterId timestamp)
        {
            UnionFind = unionFind;
            Timestamp = timestamp;
        }

        /// <summary>
        /// Initializes the handle for <paramref name="egraph"/>'s union-find. egglog's <c>UnionAction::new</c>.
        /// </summary>
        /// <param name="egraph">The e-graph.</param>
        public UnionAction(EGraph egraph)
            : this((egraph ?? throw new ArgumentNullException(nameof(egraph))).UnionFindTable, egraph.TimestampCounter)
        {

        }

        /// <summary>
        /// Gets the union-find table.
        /// </summary>
        public TableId UnionFind { get; }

        /// <summary>
        /// Gets the e-graph's timestamp counter.
        /// </summary>
        public CounterId Timestamp { get; }

        /// <summary>
        /// Stages the union of <paramref name="x"/> and <paramref name="y"/>, stamped with the current timestamp.
        /// egglog's <c>union</c>.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <param name="x">One id.</param>
        /// <param name="y">The other id.</param>
        public void Union(ExecutionState state, uint x, uint y)
        {
            ArgumentNullException.ThrowIfNull(state);
            Span<uint> row = stackalloc uint[3];
            row[0] = x;
            row[1] = y;
            row[2] = checked((uint)state.ReadCounter(Timestamp));
            state.StageInsert(UnionFind, row);
        }

    }

}
