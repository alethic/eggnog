namespace Eggnog.Relations
{

    /// <summary>
    /// The per-table step of <see cref="Database.ApplyRebuild"/>: stage the writes that rebuild one table against the
    /// rebuilder table (the union-find). egglog's closure in <c>apply_rebuild</c>
    /// (core-relations/src/free_join/mod.rs:378-385), which gives each table a fresh execution state.
    /// </summary>
    internal readonly struct RebuildTableOperation : ITableOperation
    {

        readonly TableId rebuilderId;
        readonly Table rebuilder;
        readonly uint nextTimestamp;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rebuilderId">The rebuilder table's id.</param>
        /// <param name="rebuilder">The rebuilder table, taken out of the database for the rebuild.</param>
        /// <param name="nextTimestamp">The timestamp rewritten rows get.</param>
        public RebuildTableOperation(TableId rebuilderId, Table rebuilder, uint nextTimestamp)
        {
            this.rebuilderId = rebuilderId;
            this.rebuilder = rebuilder;
            this.nextTimestamp = nextTimestamp;
        }

        /// <inheritdoc />
        public bool Run(TableId table, TableInfo info, DatabaseView view)
        {
            var state = new ExecutionState(view, nextTimestamp);
            var changed = info.Table.ApplyRebuild(rebuilderId, rebuilder, nextTimestamp, state);

            // egglog drops the state here, which flushes whatever the rebuild staged through it.
            state.Flush();
            view.Owner.RecordFailure(state);
            return changed;
        }

    }

}
