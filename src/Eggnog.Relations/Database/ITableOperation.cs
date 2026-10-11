namespace Eggnog.Relations
{

    /// <summary>
    /// A per-table step that <see cref="Database"/> runs over a list of tables, serially or in parallel: the closure
    /// egglog passes to <c>run_on_tables</c> (core-relations/src/free_join/mod.rs:412-444). Implemented by structs, so
    /// the loop is specialized per operation as Rust monomorphizes the closure.
    /// </summary>
    internal interface ITableOperation
    {

        /// <summary>
        /// Runs the step on one table, which the database has taken out of its table map for the duration. May run on
        /// several tables at once, from different threads, so it must not mutate shared state unsafely.
        /// </summary>
        /// <param name="table">The table's id.</param>
        /// <param name="info">The table and its metadata.</param>
        /// <param name="view">The database's read-only view (without the tables taken out).</param>
        /// <returns><c>true</c> if the step staged writes into the table, which the database then notifies.</returns>
        bool Run(TableId table, TableInfo info, DatabaseView view);

    }

}
