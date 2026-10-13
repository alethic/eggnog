namespace Eggnog.Relations
{

    /// <summary>
    /// The per-table step of the root's settle, which runs before the root is shared (when it creates a child or
    /// freezes) so that a child's view starts canonical: rewrite, against the root's union-find, the rows of one table
    /// written since it last rebuilt (<see cref="SortedWritesTable.SettleAppends"/>), which raw writes may have left
    /// stale. The counterpart of <see cref="RebuildTableOperation"/>, run through
    /// <see cref="Database.RunWithTakenOut{TOperation}"/> so the database notifies every table that staged a rewrite
    /// and the following merge applies it.
    /// </summary>
    /// <remarks>
    /// Each table gets a fresh execution state carrying the settle's epoch, flushed afterwards, its failure recorded, as
    /// <see cref="RebuildTableOperation"/> does. A table that is not a <see cref="SortedWritesTable"/> has nothing to
    /// settle (every function table of a store is one).
    /// </remarks>
    internal readonly struct SettleTableOperation : ITableOperation
    {

        readonly TableId unionFindId;
        readonly Table unionFind;
        readonly uint timestamp;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="unionFindId">The union-find's table id, which keys each table's rebuild mark.</param>
        /// <param name="unionFind">The root's union-find table, taken out of the database for the settle.</param>
        /// <param name="timestamp">The settle's epoch, which rewritten rows get.</param>
        public SettleTableOperation(TableId unionFindId, Table unionFind, uint timestamp)
        {
            this.unionFindId = unionFindId;
            this.unionFind = unionFind;
            this.timestamp = timestamp;
        }

        /// <inheritdoc />
        public bool Run(TableId table, TableInfo info, DatabaseView view)
        {
            if (info.Table is not SortedWritesTable sorted)
                return false;

            var state = new ExecutionState(view, timestamp);
            var changed = sorted.SettleAppends(unionFindId, unionFind, timestamp, state);
            state.Flush();
            view.Owner.RecordFailure(state);
            return changed;
        }

    }

}
