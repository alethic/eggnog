using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The column statistics the planner reads from a database: egglog's <c>ColumnCardEst</c>
    /// (core-relations/src/free_join/mod.rs:878-902). <see cref="GetColumnUniqueness"/> pairs a table's live row count
    /// with the number of distinct values its cached column index holds for the column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinct count comes from the database's cached <see cref="ColumnIndex"/> for the column, which this builds
    /// on first use (a planning side effect, as in egglog) and refreshes lazily; the index keeps values whose rows were
    /// deleted until the table's next generation, so the count may overestimate. Like egglog, this does not check that
    /// the column is cacheable.
    /// </para>
    /// <para>
    /// Planning is a cold path. Safe from any number of threads while the database does not merge.
    /// </para>
    /// </remarks>
    internal sealed class ColumnCardEstimator
    {

        readonly Database database;

        /// <summary>
        /// Initializes an estimator over <paramref name="database"/>. egglog's <c>ColumnCardEst::new</c>.
        /// </summary>
        /// <param name="database">The database.</param>
        public ColumnCardEstimator(Database database)
        {
            ArgumentNullException.ThrowIfNull(database);
            this.database = database;
        }

        /// <summary>
        /// Gets the uniqueness estimate of <paramref name="column"/> of <paramref name="table"/>: the table's live row
        /// count and the column's distinct-value count. egglog's <c>col_uniqueness</c> (mod.rs:888-895). O(1) when the
        /// column index is current; otherwise the cost of refreshing it.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="column">The column.</param>
        /// <returns>The estimate.</returns>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">No such table, or (in a child context's
        /// database) the table has no layer there.</exception>
        /// <exception cref="NotSupportedException">The database is a child context's and the table is the union-find
        /// (colored-contexts spec X23, <see cref="Database.GetQueryTableInfo"/>).</exception>
        public ColumnUniqueness GetColumnUniqueness(TableId table, ColumnId column)
        {
            var info = database.GetQueryTableInfo(table);
            var index = info.GetColumnIndex(column);
            return new ColumnUniqueness((ulong)info.Table.Count, (ulong)index.Count);
        }

    }

}
