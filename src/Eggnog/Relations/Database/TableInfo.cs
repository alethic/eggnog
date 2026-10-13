using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A table in a <see cref="Database"/> together with its metadata and cached indexes: egglog's <c>TableInfo</c>
    /// (core-relations/src/free_join/mod.rs:134-180).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The database keeps two caches per table: <see cref="Indexes"/>, hash indexes from an ordered tuple of columns to
    /// the rows holding it, and <see cref="ColumnIndexes"/>, from one column's value to its rows. Both are built on
    /// first use and refreshed lazily, at most once between two resets; <see cref="Database.MergeAll"/> resets them
    /// after the tables change, and the next use refreshes incrementally from the table's version
    /// (<see cref="Index{TIndex}.Refresh"/>). <see cref="GetIndex"/> and <see cref="GetColumnIndex"/> are egglog's
    /// <c>get_index_from_tableinfo</c> and <c>get_column_index_from_tableinfo</c> (mod.rs:833-876).
    /// </para>
    /// <para>
    /// Threading: index lookups and refreshes are safe from any number of threads while the table does not change.
    /// <see cref="ResetIndexes"/> needs exclusive access, as do the table's own writes.
    /// </para>
    /// </remarks>
    internal sealed class TableInfo
    {

        /// <summary>
        /// Initializes the entry for a newly added table, with empty index caches.
        /// </summary>
        /// <param name="name">The table's human-readable name, or <c>null</c>.</param>
        /// <param name="table">The table.</param>
        /// <param name="parallelism">The shard count and parallel build threshold of the cached indexes.</param>
        public TableInfo(string? name, Table table, ParallelHeuristics parallelism)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(parallelism);

            Name = name;
            Table = table;
            Spec = table.Spec;
            Indexes = new IndexCatalog<TupleIndex>(parallelism);
            ColumnIndexes = new IndexCatalog<ColumnIndex>(parallelism);
        }

        /// <summary>
        /// Initializes a copy, for <see cref="Clone"/>.
        /// </summary>
        TableInfo(string? name, Table table, TableSpec spec, IndexCatalog<TupleIndex> indexes, IndexCatalog<ColumnIndex> columnIndexes)
        {
            Name = name;
            Table = table;
            Spec = spec;
            Indexes = indexes;
            ColumnIndexes = columnIndexes;
        }

        /// <summary>
        /// Gets the table's human-readable name, or <c>null</c>.
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// Gets the table's schema, captured when the table was added; a table's spec never changes.
        /// </summary>
        public TableSpec Spec { get; }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public Table Table { get; }

        /// <summary>
        /// Gets the cached hash indexes on tuples of columns.
        /// </summary>
        public IndexCatalog<TupleIndex> Indexes { get; }

        /// <summary>
        /// Gets the cached hash indexes on single columns.
        /// </summary>
        public IndexCatalog<ColumnIndex> ColumnIndexes { get; }

        /// <summary>
        /// Gets the index on the tuple <paramref name="columns"/>, building or refreshing it if this is its first use
        /// since the last reset: egglog's <c>get_index_from_tableinfo</c> (core-relations/src/free_join/mod.rs:837-854).
        /// O(1) when current; O(rows appended since) to refresh; O(rows) to build.
        /// </summary>
        /// <param name="columns">The key columns, in key order; at least one.</param>
        /// <returns>The index, current with the table; valid until the next reset.</returns>
        public Index<TupleIndex> GetIndex(ReadOnlySpan<ColumnId> columns) => Indexes.GetOrRefresh(Table, columns);

        /// <summary>
        /// Gets the index on <paramref name="column"/>, building or refreshing it if this is its first use since the
        /// last reset: egglog's <c>get_column_index_from_tableinfo</c> (core-relations/src/free_join/mod.rs:859-876).
        /// </summary>
        /// <param name="column">The column.</param>
        /// <returns>The index, current with the table; valid until the next reset.</returns>
        public Index<ColumnIndex> GetColumnIndex(ColumnId column) => ColumnIndexes.GetOrRefresh(Table, column);

        /// <summary>
        /// Re-arms every cached index so that its next use refreshes it against the table: egglog's
        /// <c>update(|_, ti| ti.reset())</c> over both catalogs (core-relations/src/free_join/mod.rs:575-585, 805-814).
        /// Requires exclusive access. O(indexes).
        /// </summary>
        public void ResetIndexes()
        {
            ColumnIndexes.ResetAll();
            Indexes.ResetAll();
        }

        /// <summary>
        /// Switches both index catalogs to serial refresh, because the table's context is freezing: a frozen catalog is
        /// never reset again, and an index built lazily for a concurrent descendant is built on the thread that needs
        /// it, never by parallel work that a waiter would have to run.
        /// </summary>
        public void FreezeIndexes()
        {
            Indexes.SetSerialRefresh();
            ColumnIndexes.SetSerialRefresh();
        }

        /// <summary>
        /// Creates an independent copy: the table cloned and every cached index refreshed against this table,
        /// deep-copied and re-armed. egglog's <c>Clone for TableInfo</c> (core-relations/src/free_join/mod.rs:156-180).
        /// Requires that the table does not change meanwhile. O(rows + index sizes).
        /// </summary>
        /// <returns>The copy.</returns>
        public TableInfo Clone()
        {
            return new TableInfo(Name, Table.Clone(), Spec, Indexes.Clone(Table), ColumnIndexes.Clone(Table));
        }

    }

}
