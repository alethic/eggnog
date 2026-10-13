using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The grouping helpers egglog's <c>WrappedTableRef</c> offers (core-relations/src/table_spec.rs:778-786):
    /// <c>group_by_col</c> and <c>group_by_key</c>, which build an index over a subset of a table. The join uses them
    /// for the per-subset indexes of its trie nodes. (<c>for_each_col</c> is
    /// <see cref="Table.ForEachColumn{TSink}(SubsetRef, ColumnId, ref TSink)"/>.)
    /// </summary>
    internal static class TableIndexing
    {

        /// <summary>
        /// Groups the live rows of <paramref name="subset"/> by <paramref name="column"/>. egglog's <c>group_by_col</c>;
        /// see <see cref="ColumnIndex.BuildForSubset(Table, SubsetRef, ColumnId)"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="subset">The rows to group.</param>
        /// <param name="column">The column.</param>
        /// <returns>The index, which the caller owns.</returns>
        public static ColumnIndex GroupByColumn(this Table table, SubsetRef subset, ColumnId column) => ColumnIndex.BuildForSubset(table, subset, column);

        /// <summary>
        /// Groups the live rows of <paramref name="subset"/> by the tuple of <paramref name="columns"/>. egglog's
        /// <c>group_by_key</c>; see <see cref="TupleIndex.BuildForSubset(Table, SubsetRef, ReadOnlySpan{ColumnId})"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="subset">The rows to group.</param>
        /// <param name="columns">The key columns, in key order; at least one.</param>
        /// <returns>The index, which the caller owns.</returns>
        public static TupleIndex GroupByKey(this Table table, SubsetRef subset, ReadOnlySpan<ColumnId> columns) => TupleIndex.BuildForSubset(table, subset, columns);

        /// <summary>
        /// Groups the rows of <paramref name="subset"/> a view sees (live and not in <paramref name="hidden"/>) by
        /// <paramref name="column"/>: the visibility-aware <c>group_by_col</c> of a layered run; see
        /// <see cref="ColumnIndex.BuildForSubset(Table, SubsetRef, ColumnId, HiddenRows)"/>.
        /// </summary>
        /// <param name="table">The table: one layer of a view.</param>
        /// <param name="subset">The rows to group.</param>
        /// <param name="column">The column.</param>
        /// <param name="hidden">The rows of the layer the view hides; sealed.</param>
        /// <returns>The index, which the caller owns.</returns>
        public static ColumnIndex GroupByColumn(this Table table, SubsetRef subset, ColumnId column, HiddenRows hidden) => ColumnIndex.BuildForSubset(table, subset, column, hidden);

        /// <summary>
        /// Groups the rows of <paramref name="subset"/> a view sees by the tuple of <paramref name="columns"/>: the
        /// visibility-aware <c>group_by_key</c> of a layered run; see
        /// <see cref="TupleIndex.BuildForSubset(Table, SubsetRef, ReadOnlySpan{ColumnId}, HiddenRows)"/>.
        /// </summary>
        /// <param name="table">The table: one layer of a view.</param>
        /// <param name="subset">The rows to group.</param>
        /// <param name="columns">The key columns, in key order; at least one.</param>
        /// <param name="hidden">The rows of the layer the view hides; sealed.</param>
        /// <returns>The index, which the caller owns.</returns>
        public static TupleIndex GroupByKey(this Table table, SubsetRef subset, ReadOnlySpan<ColumnId> columns, HiddenRows hidden) => TupleIndex.BuildForSubset(table, subset, columns, hidden);

    }

}
