using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The cached indexes of one table, by ordered key columns, each refreshed lazily at most once per epoch: egglog's
    /// <c>IndexCatalog</c> (core-relations/src/hash_index/mod.rs:738-789) together with the lookup-and-refresh helpers
    /// <c>get_index_from_tableinfo</c> and <c>get_column_index_from_tableinfo</c> (core-relations/src/free_join/mod.rs:833-876).
    /// A database keeps one catalog of <see cref="TupleIndex"/>es and one of <see cref="ColumnIndex"/>es per table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Storage: a copy-on-write array of entries searched linearly, which beats a concurrent map while a table has few
    /// indexes (egglog's comment: fewer than about 64). A read is one acquire load and a scan, with no lock and no
    /// write to shared memory; adding an index takes a lock, re-checks, and publishes a longer copy (egglog uses a
    /// <c>ReadOptimizedLock&lt;Vec&gt;</c> with the same double-checked insert). Entries are never removed. Keys are
    /// ordered column lists: an index on (a, b) differs from one on (b, a), and a probe key follows the order of its
    /// index's key.
    /// </para>
    /// <para>
    /// Lifecycle: <see cref="GetOrRefresh(Table, ReadOnlySpan{ColumnId})"/> creates the index on first use and runs its
    /// refresh under the entry's <see cref="ResettableOnceLock{T}"/>: the first caller after a reset refreshes, any
    /// concurrent caller waits for it, later callers read without locking. After the table changes (egglog: at the end
    /// of <c>merge_all</c>, and in <c>clear_table</c>), <see cref="ResetAll"/> re-arms every entry; the index keeps its
    /// contents and version, so the next use refreshes incrementally.
    /// </para>
    /// <para>
    /// Threading: lookups and refreshes are safe from any number of threads while the table does not change.
    /// <see cref="ResetAll"/> requires exclusive access: no thread may be using an index of this catalog (egglog checks
    /// this with <c>Arc::get_mut(..).unwrap()</c>).
    /// </para>
    /// </remarks>
    /// <typeparam name="TIndex">The index type.</typeparam>
    internal sealed class IndexCatalog<TIndex>
        where TIndex : class, IIndexBase<TIndex>
    {

        /// <summary>
        /// Serializes inserts; created by the first insert, so a catalog that never gets an index (most of a child
        /// context's) does not pay for it.
        /// </summary>
        object? gate;
        readonly ParallelHeuristics parallelism;
        IndexCatalogEntry<TIndex>[] entries;

        /// <summary>
        /// Whether refreshes run serially (<see cref="SetSerialRefresh"/>).
        /// </summary>
        volatile bool serialRefresh;

        /// <summary>
        /// Initializes an empty catalog whose indexes use <paramref name="parallelism"/>. egglog's
        /// <c>IndexCatalog::new</c>.
        /// </summary>
        /// <param name="parallelism">The shard count and parallel build threshold of the indexes created.</param>
        public IndexCatalog(ParallelHeuristics parallelism)
        {
            ArgumentNullException.ThrowIfNull(parallelism);

            this.parallelism = parallelism;
            entries = Array.Empty<IndexCatalogEntry<TIndex>>();
        }

        /// <summary>
        /// Initializes a catalog holding <paramref name="entries"/>, for <see cref="Clone"/>.
        /// </summary>
        IndexCatalog(ParallelHeuristics parallelism, IndexCatalogEntry<TIndex>[] entries)
        {
            this.parallelism = parallelism;
            this.entries = entries;
        }

        /// <summary>
        /// Gets the parallelism settings of the indexes created.
        /// </summary>
        public ParallelHeuristics Parallelism => parallelism;

        /// <summary>
        /// Gets the number of indexes.
        /// </summary>
        public int Count => Volatile.Read(ref entries).Length;

        /// <summary>
        /// Gets a snapshot of the entries, in creation order.
        /// </summary>
        public ReadOnlySpan<IndexCatalogEntry<TIndex>> Entries => Volatile.Read(ref entries);

        /// <summary>
        /// Gets the index cell for <paramref name="key"/>, creating an empty, never refreshed index if there is none.
        /// egglog's <c>get_or_insert</c>: a lock-free scan, then on a miss a locked re-check and insert. O(indexes).
        /// </summary>
        /// <param name="key">The key columns, in key order; at least one.</param>
        /// <returns>The cell, the same one for every caller with an equal key.</returns>
        public ResettableOnceLock<Index<TIndex>> GetOrInsert(ReadOnlySpan<ColumnId> key)
        {
            if (TryFind(Volatile.Read(ref entries), key, out var found))
                return found;

            return Insert(key);
        }

        /// <summary>
        /// Gets the index over <paramref name="key"/>, creating it if needed and refreshing it against
        /// <paramref name="table"/> if this is its first use since the last <see cref="ResetAll"/>: egglog's
        /// <c>get_index_from_tableinfo</c> (core-relations/src/free_join/mod.rs:837-854).
        /// </summary>
        /// <param name="table">The table the catalog belongs to.</param>
        /// <param name="key">The key columns, in key order.</param>
        /// <returns>The index, current with the table; valid until the next <see cref="ResetAll"/>.</returns>
        public Index<TIndex> GetOrRefresh(Table table, ReadOnlySpan<ColumnId> key)
        {
            ArgumentNullException.ThrowIfNull(table);

            var cell = GetOrInsert(key);
            var index = serialRefresh
                ? cell.GetOrUpdate(table, static (ref Index<TIndex> value, Table t) => value.Refresh(t, false))
                : cell.GetOrUpdate(table, static (ref Index<TIndex> value, Table t) => value.Refresh(t));
            Debug.Assert(index.NeedsRefresh(table) == false, "a refreshed index lags its table");
            return index;
        }

        /// <summary>
        /// Gets the index over the single column <paramref name="column"/>, refreshed: egglog's
        /// <c>get_column_index_from_tableinfo</c> (core-relations/src/free_join/mod.rs:859-876).
        /// </summary>
        /// <param name="table">The table the catalog belongs to.</param>
        /// <param name="column">The key column.</param>
        /// <returns>The index, current with the table; valid until the next <see cref="ResetAll"/>.</returns>
        public Index<TIndex> GetOrRefresh(Table table, ColumnId column)
        {
            return GetOrRefresh(table, new ReadOnlySpan<ColumnId>(in column));
        }

        /// <summary>
        /// Gets whether refreshes run serially: the catalog belongs to a frozen context.
        /// </summary>
        public bool IsSerialRefresh => serialRefresh;

        /// <summary>
        /// Makes every later refresh run serially: the table's context is frozen, so its indexes may be built lazily by
        /// concurrent descendants, each on its own thread, and a build, which runs under the entry's once-lock, must
        /// never wait on parallel work that a thread waiting on that lock would have to run. Irreversible.
        /// </summary>
        public void SetSerialRefresh() => serialRefresh = true;

        /// <summary>
        /// Re-arms every index so that its next use refreshes it: egglog's <c>update(|_, ti| ti.reset())</c> at the end of
        /// <c>merge_all</c> (core-relations/src/free_join/mod.rs:575-585). Requires exclusive access. O(indexes).
        /// </summary>
        public void ResetAll()
        {
            foreach (var entry in Volatile.Read(ref entries))
                entry.Index.Reset();
        }

        /// <summary>
        /// Creates a catalog for a copy of <paramref name="table"/> with the same row ids: every index is refreshed
        /// against <paramref name="table"/>, deep-copied, and put in a fresh, re-armed cell. egglog's
        /// <c>deep_clone_map</c> in <c>Clone for TableInfo</c> (core-relations/src/free_join/mod.rs:156-180).
        /// </summary>
        /// <param name="table">The table this catalog belongs to.</param>
        /// <returns>The copy.</returns>
        public IndexCatalog<TIndex> Clone(Table table)
        {
            ArgumentNullException.ThrowIfNull(table);

            var source = Volatile.Read(ref entries);
            var copy = new IndexCatalogEntry<TIndex>[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                var index = source[i].Index.GetOrUpdate(table, static (ref Index<TIndex> value, Table t) => value.Refresh(t));
                copy[i] = new IndexCatalogEntry<TIndex>(source[i].Key.ToArray(), new ResettableOnceLock<Index<TIndex>>(index.Clone()));
            }

            return new IndexCatalog<TIndex>(parallelism, copy);
        }

        /// <summary>
        /// The locked half of <see cref="GetOrInsert"/>: re-check, then publish a copy with the new entry.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        ResettableOnceLock<Index<TIndex>> Insert(ReadOnlySpan<ColumnId> key)
        {
            lock (LazyInitializer.EnsureInitialized(ref gate))
            {
                var current = entries;
                if (TryFind(current, key, out var found))
                    return found;

                var columns = key.ToArray();
                var cell = new ResettableOnceLock<Index<TIndex>>(new Index<TIndex>(columns, TIndex.Create(columns.Length, parallelism)));
                var next = new IndexCatalogEntry<TIndex>[current.Length + 1];
                current.CopyTo(next, 0);
                next[current.Length] = new IndexCatalogEntry<TIndex>(columns, cell);

                // Release: a reader that sees the new array sees the fully built entry and cell.
                Volatile.Write(ref entries, next);
                return cell;
            }
        }

        /// <summary>
        /// Scans <paramref name="snapshot"/> for <paramref name="key"/>.
        /// </summary>
        static bool TryFind(IndexCatalogEntry<TIndex>[] snapshot, ReadOnlySpan<ColumnId> key, out ResettableOnceLock<Index<TIndex>> found)
        {
            foreach (var entry in snapshot)
            {
                if (entry.Key.SequenceEqual(key))
                {
                    found = entry.Index;
                    return true;
                }
            }

            found = null!;
            return false;
        }

    }

}
