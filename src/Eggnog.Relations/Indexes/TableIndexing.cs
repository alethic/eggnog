using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The grouping helpers egglog's <c>WrappedTableRef</c> offers (core-relations/src/table_spec.rs:778-786):
    /// <c>group_by_col</c> and <c>group_by_key</c>, which build an index over a subset of a table. The join uses them
    /// for the per-subset indexes of its trie nodes. (<c>for_each_col</c> is <see cref="Table.ForEachColumn{TSink}"/>.)
    /// </summary>
    internal static class TableIndexing
    {

        /// <summary>
        /// Groups the live rows of <paramref name="subset"/> by <paramref name="column"/>. egglog's <c>group_by_col</c>;
        /// see <see cref="ColumnIndex.BuildForSubset"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="subset">The rows to group.</param>
        /// <param name="column">The column.</param>
        /// <returns>The index, which the caller owns.</returns>
        public static ColumnIndex GroupByColumn(this Table table, SubsetRef subset, ColumnId column) => ColumnIndex.BuildForSubset(table, subset, column);

        /// <summary>
        /// Groups the live rows of <paramref name="subset"/> by the tuple of <paramref name="columns"/>. egglog's
        /// <c>group_by_key</c>; see <see cref="TupleIndex.BuildForSubset"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="subset">The rows to group.</param>
        /// <param name="columns">The key columns, in key order; at least one.</param>
        /// <returns>The index, which the caller owns.</returns>
        public static TupleIndex GroupByKey(this Table table, SubsetRef subset, ReadOnlySpan<ColumnId> columns) => TupleIndex.BuildForSubset(table, subset, columns);

    }

}
