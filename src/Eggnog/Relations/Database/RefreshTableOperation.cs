namespace Eggnog.Relations
{

    /// <summary>
    /// The per-table step of <see cref="Database.RefreshRowsForValues"/>: stage the re-insertion, with a fresh
    /// timestamp, of every row of one table that mentions a dirty id. egglog's closure in
    /// <c>refresh_rows_for_values</c> (core-relations/src/free_join/mod.rs:406-408).
    /// </summary>
    internal readonly struct RefreshTableOperation : ITableOperation
    {

        readonly uint[] dirtyIds;
        readonly uint nextTimestamp;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="dirtyIds">The ids whose meaning changed in place; not modified.</param>
        /// <param name="nextTimestamp">The timestamp refreshed rows get.</param>
        public RefreshTableOperation(uint[] dirtyIds, uint nextTimestamp)
        {
            this.dirtyIds = dirtyIds;
            this.nextTimestamp = nextTimestamp;
        }

        /// <inheritdoc />
        public bool Run(TableId table, TableInfo info, DatabaseView view) => info.Table.RefreshRowsForValues(dirtyIds, nextTimestamp);

    }

}
