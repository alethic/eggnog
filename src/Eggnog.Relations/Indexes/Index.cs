using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A secondary index kept up to date with a table by its version: egglog's <c>Index&lt;TI&gt;</c>
    /// (core-relations/src/hash_index/mod.rs:41-121). It pairs an index over chosen key columns with the table version
    /// it reflects, and <see cref="Refresh"/> brings it up to the table's current version, incrementally when it can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refresh rules, from the table's <see cref="TableVersion"/>: an unchanged version costs nothing; a new major
    /// generation (compaction or clearing renumbered the rows) clears the index and rebuilds it from every row; a grown
    /// minor version indexes just the rows appended since, which the append-only discipline makes larger than every
    /// indexed row. Large refreshes (above <see cref="ParallelHeuristics.IndexConstructionThreshold"/> rows) run in
    /// parallel, full rebuilds otherwise use the index's bulk build. The scans skip rows already deleted; rows deleted
    /// later stay listed until the next generation, so readers filter tombstones (egglog's <c>maybe_stale</c>).
    /// </para>
    /// <para>
    /// The index starts at version (0, 0), so its first refresh against a generation-zero table indexes every row
    /// incrementally, as egglog's does.
    /// </para>
    /// <para>
    /// Threading: <see cref="Refresh"/> needs exclusive access (a table's own rebuild index, or the database's
    /// <see cref="ResettableOnceLock{T}"/> in an <see cref="IndexCatalog{TIndex}"/>); afterwards any number of threads
    /// may read <see cref="Data"/> while the table does not change.
    /// </para>
    /// </remarks>
    /// <typeparam name="TIndex">The index type: <see cref="ColumnIndex"/> or <see cref="TupleIndex"/>.</typeparam>
    internal sealed class Index<TIndex>
        where TIndex : class, IIndexBase<TIndex>
    {

        readonly ColumnId[] key;
        readonly TIndex data;
        TableVersion updatedTo;

        /// <summary>
        /// Initializes an index over <paramref name="key"/> that has seen nothing (version (0, 0)). egglog's
        /// <c>Index::new</c>.
        /// </summary>
        /// <param name="key">The key columns, in key order.</param>
        /// <param name="data">The empty index to maintain.</param>
        public Index(ReadOnlySpan<ColumnId> key, TIndex data)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (key.IsEmpty)
                throw new ArgumentException("An index needs at least one key column.", nameof(key));

            this.key = key.ToArray();
            this.data = data;
        }

        /// <summary>
        /// Initializes a copy, for <see cref="Clone"/>.
        /// </summary>
        Index(ColumnId[] key, TIndex data, TableVersion updatedTo)
        {
            this.key = key;
            this.data = data;
            this.updatedTo = updatedTo;
        }

        /// <summary>
        /// Gets the key columns.
        /// </summary>
        public ReadOnlySpan<ColumnId> Key => key;

        /// <summary>
        /// Gets the table version the index reflects.
        /// </summary>
        public TableVersion UpdatedTo => updatedTo;

        /// <summary>
        /// Gets the index, to probe and iterate. Probing it directly is a non-virtual call on the sealed index type.
        /// </summary>
        public TIndex Data => data;

        /// <summary>
        /// Gets the number of distinct keys. egglog's <c>len</c>.
        /// </summary>
        public int Count => data.Count;

        /// <summary>
        /// Gets whether <see cref="Refresh"/> would change anything. egglog's <c>needs_refresh</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns><c>true</c> if the table's version differs from <see cref="UpdatedTo"/>.</returns>
        public bool NeedsRefresh(Table table)
        {
            ArgumentNullException.ThrowIfNull(table);

            return table.Version != updatedTo;
        }

        /// <summary>
        /// Brings the index up to the current version of <paramref name="table"/>. egglog's <c>refresh</c>
        /// (core-relations/src/hash_index/mod.rs:70-91). O(1) when nothing changed; O(new rows) incrementally; O(rows)
        /// after a generation change.
        /// </summary>
        /// <param name="table">The table the index was built over (or a clone of it with the same row ids).</param>
        public void Refresh(Table table)
        {
            ArgumentNullException.ThrowIfNull(table);

            var current = table.Version;
            if (current == updatedTo)
                return;

            var full = current.Major != updatedTo.Major;
            Subset subset;
            if (full)
            {
                data.Clear();
                subset = table.All();
            }
            else
            {
                subset = table.UpdatesSince(updatedTo.Minor);
            }

            if (data.Parallelism.ParallelizeIndexConstruction(subset.Size))
                data.MergeParallel(table, key, subset.AsRef());
            else if (full)
                data.RebuildFull(table, key, subset.AsRef());
            else
                data.MergeSubset(table, key, subset.AsRef());

            subset.Dispose();
            updatedTo = current;
        }

        /// <summary>
        /// Adds the live rows of <paramref name="subset"/> one at a time. egglog's <c>refresh_serial</c>
        /// (core-relations/src/hash_index/mod.rs:93-112); does not change <see cref="UpdatedTo"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="subset">The rows to add; greater than every indexed row.</param>
        public void RefreshSerial(Table table, SubsetRef subset)
        {
            data.MergeSubset(table, key, subset);
        }

        /// <summary>
        /// Creates an independent deep copy at the same version. egglog's derived <c>Clone</c>.
        /// </summary>
        /// <returns>The copy.</returns>
        public Index<TIndex> Clone() => new(key, data.Clone(), updatedTo);

    }

}
