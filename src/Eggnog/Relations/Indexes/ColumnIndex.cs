using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A hash index from one value to the rows that hold it: egglog's <c>ColumnIndex</c>
    /// (core-relations/src/hash_index/mod.rs:205-533). Built over one column it groups a table by that column (the
    /// join's per-column probes, constant constraints, distinct-value counts); built over several columns it lists each
    /// row under every value any of them holds, which is a table's rebuild index (value to the rows mentioning it).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout: shards selected by the Fx hash of the value (<see cref="ShardData.ShardOf"/>), each a SwissTable from value
    /// to <see cref="BufferedSubset"/>, stored inline, plus the arena of its sparse subsets (<see cref="ColumnIndexShard"/>).
    /// A key whose rows are contiguous costs twelve bytes of slot and no arena space.
    /// </para>
    /// <para>
    /// Contents: the rows under a key are sorted and nonempty. Rows are added in ascending order only (append-only
    /// discipline). A row is listed once under each distinct value of its covered columns: egglog lists it once per
    /// column, so a row holding the same value twice appears twice under that value except after a sort-based rebuild,
    /// which deduplicates; here every path deduplicates, which makes the incremental, parallel and sort-based builds
    /// agree. Rows deleted after they were indexed stay listed until the table's next generation, so readers filter
    /// tombstones.
    /// </para>
    /// <para>
    /// Building: one row at a time (<see cref="AddRow(uint, int)"/>, <see cref="MergeSubset"/>), by sorting
    /// (<see cref="RebuildFull(Table, ReadOnlySpan{ColumnId}, SubsetRef)"/>), or in parallel
    /// (<see cref="MergeParallel"/>); <see cref="Index{TIndex}"/> picks the path. Probing (<see cref="TryGetSubset"/>)
    /// is a non-virtual, inlinable call on this sealed class.
    /// </para>
    /// <para>
    /// Threading: any number of readers while nobody writes; one writer at a time. A parallel build splits work so
    /// that each shard has one writer.
    /// </para>
    /// </remarks>
    internal sealed partial class ColumnIndex : IIndexBase<ColumnIndex>, IDisposable
    {

        /// <summary>
        /// Subsets of at least this many rows are grouped by sorting rather than row by row: egglog's
        /// <c>SORT_BULK_THRESHOLD</c> (core-relations/src/hash_index/mod.rs:521).
        /// </summary>
        internal const int SortBulkThreshold = 512;

        /// <summary>
        /// The number of columns whose one-element projection <see cref="CheckColumns"/> shares.
        /// </summary>
        const int SharedProjectionCount = 64;

        /// <summary>
        /// The shared one-element projections, by column, created on first use.
        /// </summary>
        static readonly int[]?[] SharedProjections = new int[]?[SharedProjectionCount];

        readonly ParallelHeuristics parallelism;
        readonly ShardData shardData;
        readonly ColumnIndexShard[] shards;

        /// <summary>
        /// Initializes an empty index with the shard count of <paramref name="parallelism"/>: egglog's
        /// <c>ColumnIndex::new</c>, whose shard count is <c>num_shards()</c> (one when single-threaded, otherwise twice
        /// the thread count). Rents and allocates no per-shard storage until a shard receives its first value.
        /// </summary>
        /// <param name="parallelism">The parallelism settings.</param>
        public ColumnIndex(ParallelHeuristics parallelism)
        {
            ArgumentNullException.ThrowIfNull(parallelism);

            this.parallelism = parallelism;
            shardData = parallelism.ShardData;
            shards = new ColumnIndexShard[shardData.Count];
            for (var i = 0; i < shards.Length; i++)
                shards[i] = ColumnIndexShard.Create();
        }

        /// <summary>
        /// Initializes an index over existing shards, for <see cref="Clone"/>.
        /// </summary>
        ColumnIndex(ParallelHeuristics parallelism, ColumnIndexShard[] shards)
        {
            this.parallelism = parallelism;
            shardData = parallelism.ShardData;
            this.shards = shards;
        }

        /// <inheritdoc />
        public static ColumnIndex Create(int keyArity, ParallelHeuristics parallelism) => new(parallelism);

        /// <inheritdoc />
        public ParallelHeuristics Parallelism => parallelism;

        /// <summary>
        /// Gets the shard layout.
        /// </summary>
        public ShardData ShardData => shardData;

        /// <summary>
        /// Gets the number of shards.
        /// </summary>
        public int ShardCount => shards.Length;

        /// <summary>
        /// Gets the number of distinct values. egglog's <c>len</c>; the planner uses it as a column's distinct count.
        /// O(shards).
        /// </summary>
        public int Count
        {
            get
            {
                var total = 0;
                foreach (ref readonly var shard in shards.AsSpan())
                    total += shard.Count;

                return total;
            }
        }

        /// <summary>
        /// Looks up the rows listed under <paramref name="key"/>. egglog's <c>get_subset</c>. O(1) expected: a hash, one
        /// SwissTable probe and one load; no allocation.
        /// </summary>
        /// <param name="key">The value.</param>
        /// <param name="rows">The rows, sorted and nonempty, valid until the index is next modified; may include rows
        /// deleted since they were indexed.</param>
        /// <returns><c>true</c> if any row is listed under the value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetSubset(uint key, out SubsetRef rows)
        {
            var hash = FxHasher.Hash(key);
            return shards[shardData.ShardOf(hash)].TryGetSubset(key, hash, out rows);
        }

        /// <summary>
        /// Lists <paramref name="row"/> under <paramref name="value"/>; <paramref name="row"/> must be at least every row
        /// already listed under it. Amortized O(1).
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="row">The row id.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(uint value, int row)
        {
            var hash = FxHasher.Hash(value);
            shards[shardData.ShardOf(hash)].AddRow(value, hash, row);
        }

        /// <summary>
        /// Lists <paramref name="row"/> under each distinct value of <paramref name="values"/>: egglog's
        /// <c>add_row(&amp;[Value], row)</c> (core-relations/src/hash_index/mod.rs:232-244), which lists it once per
        /// value, repeats included. O(values).
        /// </summary>
        /// <param name="values">The values (egglog's write key: the row's covered columns).</param>
        /// <param name="row">The row id.</param>
        public void AddRow(ReadOnlySpan<uint> values, int row)
        {
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (i == 0 || values[..i].Contains(value) == false)
                    AddRow(value, row);
            }
        }

        /// <summary>
        /// Lists <paramref name="rowId"/> under each distinct value of the given columns of <paramref name="row"/>.
        /// </summary>
        /// <param name="row">The row's cells.</param>
        /// <param name="columns">The covered columns.</param>
        /// <param name="rowId">The row id.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRowColumns(ReadOnlySpan<uint> row, int[] columns, int rowId)
        {
            for (var i = 0; i < columns.Length; i++)
            {
                if (RepeatsEarlierColumn(row, columns, i) == false)
                    AddRow(row[columns[i]], rowId);
            }
        }

        /// <summary>
        /// Adds every row of a tagged buffer under each distinct value of its cells: egglog's <c>merge_rows</c>.
        /// </summary>
        /// <param name="buffer">Rows of covered values, tagged with their row ids, in ascending row order.</param>
        public void MergeRows(TaggedRowBuffer buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            for (var i = 0; i < buffer.Count; i++)
            {
                var values = buffer.GetRow(i, out var rowId);
                AddRow(values, rowId);
            }
        }

        /// <summary>
        /// Visits every value and its rows: shards in order, values in hash-table slot order within a shard (egglog's
        /// <c>IndexMap</c> visits them in insertion order; no caller depends on the order). egglog's <c>for_each</c>.
        /// O(values + slots), no allocation.
        /// </summary>
        /// <typeparam name="TVisitor">The visitor type.</typeparam>
        /// <param name="visitor">The visitor.</param>
        public void ForEach<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IColumnIndexVisitor
        {
            foreach (ref readonly var shard in shards.AsSpan())
            {
                var arena = shard.Arena;
                foreach (ref var entry in shard.Table)
                    visitor.Visit(entry.Key, entry.Subset.AsRef(arena));
            }
        }

        /// <summary>
        /// Gets the first entry of shard <paramref name="shard"/> at or after hash slot <paramref name="slot"/>, for
        /// external iteration (<see cref="ColumnIndexCursor"/>).
        /// </summary>
        /// <param name="shard">The shard, in <c>[0, ShardCount)</c>.</param>
        /// <param name="slot">The slot to start from; at least zero.</param>
        /// <param name="key">The value.</param>
        /// <param name="rows">Its rows.</param>
        /// <returns>The entry's slot, or -1 if the shard has no entry at or after <paramref name="slot"/>.</returns>
        public int NextEntry(int shard, int slot, out uint key, out SubsetRef rows)
        {
            ref readonly var s = ref shards[shard];
            var found = s.Table.NextFullSlot(slot);
            if (found < 0)
            {
                key = 0;
                rows = default;
                return -1;
            }

            var entry = s.Table.GetEntry(found);
            key = entry.Key;
            rows = entry.Subset.AsRef(s.Arena);
            return found;
        }

        /// <summary>
        /// Removes every value, keeping the storage. egglog's <c>clear</c>. O(hash slots).
        /// </summary>
        public void Clear()
        {
            for (var i = 0; i < shards.Length; i++)
                shards[i].Clear();
        }

        /// <summary>
        /// Makes room for the values of <paramref name="rows"/> rows spread over the shards, so that a small build does
        /// not rehash: egglog's <c>reserve_for_n_rows</c> (core-relations/src/hash_index/mod.rs:503-511).
        /// </summary>
        /// <param name="rows">The number of rows about to be added.</param>
        public void ReserveForRows(int rows)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(rows);

            var perShard = rows / shards.Length + 2;
            for (var i = 0; i < shards.Length; i++)
                shards[i].Reserve(perShard, 0);
        }

        /// <inheritdoc />
        public void MergeSubset(Table table, ReadOnlySpan<ColumnId> columns, SubsetRef subset)
        {
            var projection = CheckColumns(table, columns);
            if (subset.IsEmpty)
                return;

            var sink = new ColumnIndexInsertSink(this, projection);
            table.ScanGeneric(subset, ref sink);
        }

        /// <summary>
        /// Groups the live rows of <paramref name="subset"/> of <paramref name="table"/> by <paramref name="column"/>:
        /// egglog's <c>build_for_subset</c> (core-relations/src/hash_index/mod.rs:513-532), behind <c>group_by_col</c>.
        /// Subsets of at least <see cref="SortBulkThreshold"/> rows are grouped by sorting, smaller ones row by row.
        /// </summary>
        /// <remarks>
        /// egglog shards a per-subset index like any other. Such an index is never built or refreshed in parallel, and
        /// shards only serve parallel builds, so this one has a single shard: the same contents at a fraction of the
        /// allocations, which matters because the join builds one per probed trie node.
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <param name="subset">The rows to group.</param>
        /// <param name="column">The column to group by.</param>
        /// <returns>The index, which the caller owns. Disposing it when done (egglog drops it with its trie node) hands
        /// its storage back to the pools, so that the next build reuses it.</returns>
        public static ColumnIndex BuildForSubset(Table table, SubsetRef subset, ColumnId column)
        {
            ArgumentNullException.ThrowIfNull(table);

            var index = new ColumnIndex(ParallelHeuristics.Serial);
            index.RebuildForSubset(table, subset, column);
            return index;
        }

        /// <summary>
        /// Groups the rows of <paramref name="subset"/> of <paramref name="table"/> a view sees (live and not in
        /// <paramref name="hidden"/>) by <paramref name="column"/>: the visibility-aware
        /// <see cref="BuildForSubset(Table, SubsetRef, ColumnId)"/> of a layered run's per-subset indexes
        /// (colored-contexts spec §9.3), whose probe results therefore need no further visibility refinement. With
        /// <see cref="HiddenRows.None"/> it is the plain build.
        /// </summary>
        /// <param name="table">The table: one layer of a view.</param>
        /// <param name="subset">The rows to group.</param>
        /// <param name="column">The column to group by.</param>
        /// <param name="hidden">The rows of the layer the view hides; sealed (I18).</param>
        /// <returns>The index, which the caller owns; dispose it when done.</returns>
        public static ColumnIndex BuildForSubset(Table table, SubsetRef subset, ColumnId column, HiddenRows hidden)
        {
            if (hidden.IsNone)
                return BuildForSubset(table, subset, column);

            ArgumentNullException.ThrowIfNull(table);
            var index = new ColumnIndex(ParallelHeuristics.Serial);
            index.RebuildForSubset(table, subset, column, hidden);
            return index;
        }

        /// <summary>
        /// Empties this index and refills it with the live rows of <paramref name="subset"/> of
        /// <paramref name="table"/> grouped by <paramref name="column"/>:
        /// <see cref="BuildForSubset(Table, SubsetRef, ColumnId)"/> into an existing index, typically one from
        /// <see cref="BuildForSubset(Table, SubsetRef, ColumnId)"/> that was disposed. The join's trie nodes keep their
        /// per-column indexes across reuse this way (egglog allocates an <c>Arc&lt;ColumnIndex&gt;</c> and its shard
        /// vector per build), so a recycled node's build takes its hash table and arena from the pools and allocates
        /// nothing once they are warm.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="subset">The rows to group.</param>
        /// <param name="column">The column to group by.</param>
        public void RebuildForSubset(Table table, SubsetRef subset, ColumnId column)
        {
            ArgumentNullException.ThrowIfNull(table);

            Clear();
            ReadOnlySpan<ColumnId> columns = [column];
            if (subset.Size >= SortBulkThreshold)
            {
                RebuildFull(table, columns, subset);
            }
            else
            {
                ReserveForRows(subset.Size);
                MergeSubset(table, columns, subset);
            }
        }

        /// <summary>
        /// Empties this index and refills it with the rows of <paramref name="subset"/> of <paramref name="table"/> a
        /// view sees, grouped by <paramref name="column"/>: the visibility-aware
        /// <see cref="RebuildForSubset(Table, SubsetRef, ColumnId)"/> (colored-contexts spec §9.3), with the same choice
        /// between the sort-based and the row-at-a-time build. With <see cref="HiddenRows.None"/> it is the plain
        /// rebuild.
        /// </summary>
        /// <param name="table">The table: one layer of a view.</param>
        /// <param name="subset">The rows to group.</param>
        /// <param name="column">The column to group by.</param>
        /// <param name="hidden">The rows of the layer the view hides; sealed (I18).</param>
        public void RebuildForSubset(Table table, SubsetRef subset, ColumnId column, HiddenRows hidden)
        {
            if (hidden.IsNone)
            {
                RebuildForSubset(table, subset, column);
                return;
            }

            ArgumentNullException.ThrowIfNull(table);
            Clear();
            ReadOnlySpan<ColumnId> columns = [column];
            if (subset.Size >= SortBulkThreshold)
            {
                RebuildFull(table, columns, subset, hidden);
            }
            else
            {
                ReserveForRows(subset.Size);
                MergeSubsetVisible(table, columns, subset, hidden);
            }
        }

        /// <summary>
        /// Adds the rows of <paramref name="subset"/> of <paramref name="table"/> a view sees (live and not in
        /// <paramref name="hidden"/>), one row at a time: the visibility-aware <see cref="MergeSubset"/> for per-subset
        /// builds (colored-contexts spec §9.3). Not part of <see cref="IIndexBase{TSelf}"/>: a catalog index lists every
        /// live row of its layer, because hides belong to views, not to index content (C6), so
        /// <see cref="Index{TIndex}"/> never calls this. With <see cref="HiddenRows.None"/> it is
        /// <see cref="MergeSubset"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="columns">The key columns.</param>
        /// <param name="subset">The rows to add; greater than every row already indexed.</param>
        /// <param name="hidden">The rows the view hides; sealed (I18).</param>
        internal void MergeSubsetVisible(Table table, ReadOnlySpan<ColumnId> columns, SubsetRef subset, HiddenRows hidden)
        {
            if (hidden.IsNone)
            {
                MergeSubset(table, columns, subset);
                return;
            }

            var projection = CheckColumns(table, columns);
            if (subset.IsEmpty)
                return;

            var sink = new ColumnIndexInsertSink(this, projection);
            table.ScanGeneric(subset, hidden, ref sink);
        }

        /// <inheritdoc />
        public ColumnIndex Clone()
        {
            var copy = new ColumnIndexShard[shards.Length];
            for (var i = 0; i < shards.Length; i++)
                copy[i] = shards[i].Clone();

            return new ColumnIndex(parallelism, copy);
        }

        /// <summary>
        /// Hands every shard's hash table and arena back to the engine's pools (<see cref="ColumnIndexHashPolicy"/>,
        /// <see cref="SubsetArenaPolicy"/>), as dropping egglog's index returns its pooled maps and arenas, and empties
        /// the index. It stays usable and rents again on the next value; disposing twice is harmless. Subsets taken from
        /// it must not be used afterwards. Optional: an index that is simply dropped is reclaimed by the garbage
        /// collector, and its storage is not reused. Allocates nothing; O(shards) plus clearing each table's control
        /// bytes.
        /// </summary>
        public void Dispose()
        {
            for (var i = 0; i < shards.Length; i++)
                shards[i].Release();
        }

        /// <summary>
        /// Gets whether the value in column <c>columns[i]</c> of <paramref name="row"/> already appears in an earlier
        /// covered column, so the row is listed under it once. O(i).
        /// </summary>
        /// <param name="row">The row's cells.</param>
        /// <param name="columns">The covered columns.</param>
        /// <param name="i">The position of the column in <paramref name="columns"/>.</param>
        /// <returns><c>true</c> if the value repeats.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool RepeatsEarlierColumn(ReadOnlySpan<uint> row, int[] columns, int i)
        {
            var value = row[columns[i]];
            for (var j = 0; j < i; j++)
            {
                if (row[columns[j]] == value)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks that <paramref name="columns"/> is a nonempty list of columns of <paramref name="table"/> and returns
        /// it as column indices. A single column below <see cref="SharedProjectionCount"/>, the per-subset builds of the
        /// join, gets a shared array, so the check allocates nothing; callers only read the result.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="columns">The columns.</param>
        /// <returns>The column indices; shared for a single column, so never written.</returns>
        internal static int[] CheckColumns(Table table, ReadOnlySpan<ColumnId> columns)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (columns.IsEmpty)
                throw new ArgumentException("An index needs at least one column.", nameof(columns));

            if (columns.Length == 1)
                return SingleColumnProjection(CheckColumn(table, columns[0]));

            var projection = new int[columns.Length];
            for (var i = 0; i < columns.Length; i++)
                projection[i] = CheckColumn(table, columns[i]);

            return projection;
        }

        /// <summary>
        /// Checks that <paramref name="column"/> is a column of <paramref name="table"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="column">The column.</param>
        /// <returns>The column's index.</returns>
        static int CheckColumn(Table table, ColumnId column)
        {
            var index = column.Rep;
            if ((uint)index >= (uint)table.Arity)
                throw new ArgumentOutOfRangeException("columns", index, $"The table has {table.Arity} columns.");

            return index;
        }

        /// <summary>
        /// Gets the one-element projection of <paramref name="column"/>: a shared array, created on first use, for
        /// columns below <see cref="SharedProjectionCount"/>, and a new one otherwise.
        /// </summary>
        /// <param name="column">The column's index; not negative.</param>
        /// <returns>The projection; never written.</returns>
        static int[] SingleColumnProjection(int column)
        {
            if (column >= SharedProjectionCount)
                return [column];

            var projection = Volatile.Read(ref SharedProjections[column]);
            if (projection is not null)
                return projection;

            int[] created = [column];
            return Interlocked.CompareExchange(ref SharedProjections[column], created, null) ?? created;
        }

    }

}
