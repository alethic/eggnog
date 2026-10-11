using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A keyed table with a merge function, written in batches and optionally kept sorted by one column: egglog's
    /// <c>SortedWritesTable</c> (core-relations/src/table/mod.rs), the table behind every egglog function.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout. Rows live in one append-only, row-major <see cref="RowBuffer"/>; a deleted or superseded row is a
    /// tombstone (<see cref="Value.StaleRep"/> in its first cell) until the table compacts. A sharded SwissTable maps
    /// each live key to its one live row (<see cref="TableEntry"/>: the key's full hash and the row id; the key is
    /// compared in place). When the table is sorted by a column (the timestamp, in egglog), that column is
    /// nondecreasing in row order, and a run-length index of it, (value, first row) pairs strictly increasing in both,
    /// turns range constraints on it into dense row ranges in O(log runs): that is what makes seminaive deltas cheap.
    /// </para>
    /// <para>
    /// Writes. Writers stage rows and removal keys into <see cref="MutationBuffer"/>s, pre-sharded by key hash, and
    /// publish them to per-shard queues. <see cref="Merge"/> applies every staged removal, then every staged insert,
    /// then compacts if more than half the rows (and more than 16) are tombstones. An insert whose key is new is
    /// appended. One whose key exists goes through the <see cref="MergeFunction"/>: if it returns <c>true</c> its
    /// output is appended and the old row tombstoned, otherwise nothing is written (the old row keeps its timestamp, so
    /// no delta appears). Rows of one key fold in staging order. Because removals come first, staging
    /// <c>remove(k)</c> and <c>insert(k, row)</c> in one batch replaces the row without calling the merge function;
    /// rebuild relies on that.
    /// </para>
    /// <para>
    /// Above <see cref="ParallelHeuristics.TableOpThreshold"/> rows, deletes, inserts and compaction run one task per
    /// shard. The parallel insert stores the merge function's output and reports only real changes, fixing two egglog
    /// defects (core-relations/src/table/mod.rs:871 and 902-907; map §8.1 rows 3 and 4). It pre-folds each shard's
    /// batch before merging into the table, which agrees with the serial fold only for associative merges, and
    /// requires every row of one parallel merge to carry the same sort value, which it checks before writing anything.
    /// </para>
    /// <para>
    /// Failures. When a sort check fails or a merge function throws, <see cref="Merge"/> propagates the exception with
    /// the table consistent: rows, key index, stale count and runs agree, and no tombstone is visible. egglog panics
    /// instead, leaving the table half-written. The failed batch may be partly applied (a parallel sort violation
    /// applies none of it), and the staged inserts not applied are dropped.
    /// </para>
    /// <para>
    /// Reads. Every read that yields rows to a caller skips tombstones, including scans with constraints, which egglog
    /// does not filter (core-relations/src/table/mod.rs:429-439, 1018-1025; map §8.1 row 1). <see cref="All"/>,
    /// <see cref="UpdatesSince"/> and <see cref="TryFastSubset"/> return physical ranges that may include tombstones;
    /// whoever reads them through this table gets only live rows.
    /// </para>
    /// <para>
    /// Versions. <see cref="TableVersion.Minor"/> is the physical row count, so the rows appended since a version are a
    /// suffix; removals change no version. Compaction and clearing a nonempty table start a new generation, which
    /// invalidates every row id. Clones are independent of their source.
    /// </para>
    /// <para>
    /// Threading: reads run concurrently while the table is not being merged; <see cref="Merge"/>, <see cref="Clear"/>,
    /// <see cref="ApplyRebuild"/> and <see cref="RefreshRowsForValues"/> are exclusive. During a parallel phase each
    /// shard's rows are touched by exactly one task.
    /// </para>
    /// </remarks>
    internal sealed partial class SortedWritesTable : Table
    {

        /// <summary>
        /// The number of distinct keys one batch of a parallel insert task folds before the task starts another batch;
        /// each batch is appended to the table in one block. egglog's <c>BATCH_SIZE</c>
        /// (core-relations/src/table/mod.rs:822).
        /// </summary>
        internal const int ParallelInsertBatchSize = 1 << 18;

        readonly int keyCount;
        readonly int sortColumn;
        readonly int[] toRebuild;
        readonly MergeFunction merge;
        readonly ParallelHeuristics parallelism;
        readonly TableSpec spec;
        readonly ShardedHashTable<TableEntry> hash;
        readonly uint[] mergeOutput;
        RowBuffer data;
        RowBuffer scratch;
        int staleRows;
        int generation;
        uint[] runValues;
        int[] runStarts;
        int runCount;
        PendingState pending;
        SubsetTracker subsetTracker;

        /// <summary>
        /// Initializes an empty table. egglog's <c>SortedWritesTable::new</c>.
        /// </summary>
        /// <param name="keyCount">The number of key columns; may be zero (then the table holds at most one row).</param>
        /// <param name="arity">The number of columns; at least one and at least <paramref name="keyCount"/>.</param>
        /// <param name="sortBy">The column kept nondecreasing in row order (the timestamp), or <c>null</c>.</param>
        /// <param name="toRebuild">The columns holding ids that a rebuild canonicalizes.</param>
        /// <param name="merge">Resolves conflicts between rows with the same key.</param>
        /// <param name="parallelism">When to go parallel; also fixes the shard count. <see cref="ParallelHeuristics.Default"/> if <c>null</c>.</param>
        public SortedWritesTable(int keyCount, int arity, ColumnId? sortBy, ReadOnlySpan<ColumnId> toRebuild, MergeFunction merge, ParallelHeuristics? parallelism = null)
            : base(TableKind.SortedWrites, arity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(arity, 1);
            ArgumentOutOfRangeException.ThrowIfNegative(keyCount);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(keyCount, arity);
            ArgumentNullException.ThrowIfNull(merge);
            if (sortBy is { } sort && (uint)sort.Rep >= (uint)arity)
                throw new ArgumentOutOfRangeException(nameof(sortBy), sort, "The sort column is not part of the table.");

            this.toRebuild = new int[toRebuild.Length];
            for (var i = 0; i < toRebuild.Length; i++)
            {
                if ((uint)toRebuild[i].Rep >= (uint)arity)
                    throw new ArgumentOutOfRangeException(nameof(toRebuild), toRebuild[i], "The column is not part of the table.");

                this.toRebuild[i] = toRebuild[i].Rep;
            }

            this.keyCount = keyCount;
            sortColumn = sortBy?.Rep ?? -1;
            this.merge = merge;
            this.parallelism = parallelism ?? ParallelHeuristics.Default;
            spec = new TableSpec(keyCount, arity - keyCount, true);
            hash = new ShardedHashTable<TableEntry>(this.parallelism.ShardData);
            mergeOutput = new uint[arity];
            data = new RowBuffer(arity);
            scratch = new RowBuffer(arity);
            runValues = Array.Empty<uint>();
            runStarts = Array.Empty<int>();
            pending = new PendingState(hash.ShardData);
            subsetTracker = new SubsetTracker();
        }

        /// <summary>
        /// Initializes a copy of <paramref name="source"/>: egglog's <c>Clone for SortedWritesTable</c>
        /// (core-relations/src/table/mod.rs:154-171). Rows, key index and runs are copied, staged writes are copied
        /// into a new pending state, the merge function is shared, and the rebuild bookkeeping starts over.
        /// </summary>
        /// <param name="source">The table to copy.</param>
        SortedWritesTable(SortedWritesTable source)
            : base(TableKind.SortedWrites, source.Arity)
        {
            keyCount = source.keyCount;
            sortColumn = source.sortColumn;
            toRebuild = source.toRebuild;
            merge = source.merge;
            parallelism = source.parallelism;
            spec = source.spec;
            hash = source.hash.Clone();
            mergeOutput = new uint[source.Arity];
            data = source.data.Clone();
            scratch = new RowBuffer(source.Arity);
            staleRows = source.staleRows;
            generation = source.generation;
            runValues = source.runValues.AsSpan(0, source.runCount).ToArray();
            runStarts = source.runStarts.AsSpan(0, source.runCount).ToArray();
            runCount = source.runCount;
            pending = source.pending.DeepCopy();
            subsetTracker = new SubsetTracker();
        }

        /// <inheritdoc />
        public override TableSpec Spec => spec;

        /// <summary>
        /// Gets the number of key columns.
        /// </summary>
        public int KeyCount => keyCount;

        /// <summary>
        /// Gets the sort column, or -1 when the table is unsorted.
        /// </summary>
        public int SortColumn => sortColumn;

        /// <summary>
        /// Gets the merge function.
        /// </summary>
        public MergeFunction MergeFunction => merge;

        /// <summary>
        /// Gets the parallelism settings the table was created with.
        /// </summary>
        public ParallelHeuristics Parallelism => parallelism;

        /// <summary>
        /// Gets the row storage, for code that reads rows directly (compiled plans): row <c>r</c> starts at
        /// <c>Rows.Data[r * Arity]</c>; rows whose first cell is <see cref="Value.StaleRep"/> are tombstones. The
        /// buffer and its array are replaced by merges, compaction and clearing, never during a read phase, so read
        /// them once per scan.
        /// </summary>
        public RowBuffer Rows => data;

        /// <summary>
        /// Gets the number of tombstones in <see cref="Rows"/>; zero means scans need no liveness check.
        /// </summary>
        public int StaleCount => staleRows;

        /// <summary>
        /// Gets the number of runs of equal values in the sort column (zero for an unsorted table).
        /// </summary>
        public int SortRunCount => runCount;

        /// <inheritdoc />
        public override int Count => data.Count - staleRows;

        /// <inheritdoc />
        public override TableVersion Version => new(generation, data.Count);

        /// <inheritdoc />
        public override bool HasStaleRows => staleRows > 0;

        /// <inheritdoc />
        public override Subset All() => Subset.Dense(0, data.Count);

        /// <inheritdoc />
        public override Subset UpdatesSince(int offset)
        {
            if ((uint)offset > (uint)data.Count)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, $"The table has {data.Count} physical rows.");

            return Subset.Dense(offset, data.Count);
        }

        /// <inheritdoc />
        public override MutationBuffer NewBuffer() => new(pending, Arity, keyCount, true);

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>merge</c> (core-relations/src/table/mod.rs:530-535): every staged removal, then every staged
        /// insert, then a compaction if tombstones dominate. <see cref="TableChange.Added"/> is true only if a key was
        /// added or a merge function returned <c>true</c>; staging done by merge functions sets
        /// <see cref="ExecutionState.Changed"/>, in the parallel path too.
        /// </remarks>
        public override TableChange Merge(ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            var removed = DoDelete();
            var added = DoInsert(state);
            MaybeRehash();
            return new TableChange(added, removed);
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>clear</c> (core-relations/src/table/mod.rs:296-305): staged writes are dropped, and a nonempty
        /// table loses its rows, key index and runs and moves to a new generation. The staged-row totals are reset too,
        /// which egglog leaves alone.
        /// </remarks>
        public override void Clear()
        {
            pending.Clear();
            if (data.Count == 0)
                return;

            runCount = 0;
            data.Clear();
            staleRows = 0;
            hash.Clear();
            generation++;
        }

        /// <inheritdoc />
        public override Table Clone() => new SortedWritesTable(this);

        /// <summary>
        /// Tombstones <paramref name="row"/>, counting it if it was live. egglog's <c>Rows::set_stale</c>.
        /// </summary>
        /// <param name="row">A row id.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SetStale(int row)
        {
            if (data.SetStale(row) == false)
                staleRows++;
        }

        /// <summary>
        /// Throws if a row with sort value <paramref name="value"/> would break the sort order: egglog's
        /// <c>assert!(sort_val &gt;= largest)</c> (core-relations/src/table/mod.rs:727-730), checked here before anything is
        /// written so the table stays consistent.
        /// </summary>
        /// <param name="value">The sort value of the row about to be appended.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckSortValue(uint value)
        {
            if (runCount > 0 && value < runValues[runCount - 1])
                ThrowSortOrder(value, runValues[runCount - 1]);
        }

        /// <summary>
        /// Records that <paramref name="row"/>, just appended, has sort value <paramref name="value"/>: a new run starts
        /// when the value exceeds the last run's. egglog's offsets bookkeeping (core-relations/src/table/mod.rs:724-736).
        /// </summary>
        /// <param name="value">The row's sort value; at least the last run's.</param>
        /// <param name="row">The row id.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void RecordSortValue(uint value, int row)
        {
            Debug.Assert(runCount == 0 || value >= runValues[runCount - 1]);
            if (runCount == 0 || value > runValues[runCount - 1])
                PushRun(value, row);
        }

        /// <summary>
        /// Appends the run (<paramref name="value"/>, <paramref name="start"/>).
        /// </summary>
        void PushRun(uint value, int start)
        {
            if (runCount == runValues.Length)
            {
                var capacity = Math.Max(8, runValues.Length * 2);
                Array.Resize(ref runValues, capacity);
                Array.Resize(ref runStarts, capacity);
            }

            Debug.Assert(runCount == 0 || (runValues[runCount - 1] < value && runStarts[runCount - 1] < start));
            runValues[runCount] = value;
            runStarts[runCount] = start;
            runCount++;
        }

        /// <summary>
        /// Debug-checks that a merge output keeps the incoming row's key and sort value, as the table's index and runs
        /// assume.
        /// </summary>
        [Conditional("DEBUG")]
        void AssertMergeOutput(ReadOnlySpan<uint> incoming, ReadOnlySpan<uint> output)
        {
            Debug.Assert(incoming[..keyCount].SequenceEqual(output[..keyCount]), "A merge function changed the key.");
            Debug.Assert(sortColumn < 0 || incoming[sortColumn] == output[sortColumn], "A merge function changed the sort value.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowSortOrder(uint value, uint largest)
        {
            throw new InvalidOperationException($"Inserting a row that violates the sort order ({value} after {largest}).");
        }

    }

}
