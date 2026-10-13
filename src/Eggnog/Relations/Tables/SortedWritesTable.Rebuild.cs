using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    internal sealed partial class SortedWritesTable
    {

        /// <summary>
        /// The number of rows a full rebuild hands the rebuilder at once: egglog's <c>STEP_SIZE</c>
        /// (core-relations/src/table/rebuild.rs:203).
        /// </summary>
        internal const int RebuildStepSize = 2048;

        /// <summary>
        /// The number of rows the last rebuild handed its rebuilder; see <see cref="LastRebuildRows"/>.
        /// </summary>
        int lastRebuildRows;

        /// <summary>
        /// Whether the last rebuild took the incremental path; see <see cref="LastRebuildWasIncremental"/>.
        /// </summary>
        bool lastRebuildIncremental;

        /// <summary>
        /// Gets the columns a rebuild canonicalizes.
        /// </summary>
        public ReadOnlySpan<ColumnId> RebuildColumns => MemoryMarshal.Cast<int, ColumnId>(toRebuild.AsSpan());

        /// <summary>
        /// Gets the number of rows the last <see cref="ApplyRebuild"/> (or <see cref="SettleAppends"/>) handed its
        /// rebuilder: the rows gathered by the incremental path, or the physical rows the full path scanned. For
        /// diagnostics and tests (colored-contexts spec §17 M9.4: a child's incremental rebuild examines only the rows
        /// naming its new losers); one field store per rebuild.
        /// </summary>
        internal int LastRebuildRows => lastRebuildRows;

        /// <summary>
        /// Gets whether the last <see cref="ApplyRebuild"/> took the incremental path. For diagnostics and tests.
        /// </summary>
        internal bool LastRebuildWasIncremental => lastRebuildIncremental;

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// egglog's <c>do_rebuild</c> (core-relations/src/table/rebuild.rs:45-75). For every row that the rebuilder
        /// changes, stages a removal of the row's current key and an insertion of the rewritten row stamped with
        /// <paramref name="nextTimestamp"/> in the sort column; the next <see cref="Merge"/> applies them, removals
        /// first, so a rewritten row whose key collides with another goes through the merge function (congruence).
        /// </para>
        /// <para>
        /// When the rebuilder names a hint column, the table first advances its <see cref="SubsetTracker"/> over the
        /// providing table (before choosing a mode, as egglog does) and chooses the incremental path when the table has
        /// more than 10000 physical rows and the provider's new rows are few (at most an eighth, or 1/8192 when
        /// parallel). Both paths stage the same writes (see <see cref="RebuildIncremental"/>).
        /// </para>
        /// <para>
        /// Either way the table then records its version against <paramref name="rebuilderTableId"/>
        /// (<see cref="MarkRebuilt"/>), so that the next incremental rebuild against the same rebuilder also rewrites the
        /// rows written after this one. The mark is kept per rebuilder, as the <see cref="SubsetTracker"/> positions it
        /// pairs with are: a rebuild against another rebuilder canonicalizes nothing with respect to this one.
        /// </para>
        /// </remarks>
        public override bool ApplyRebuild(TableId rebuilderTableId, Table rebuilderTable, uint nextTimestamp, ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(rebuilderTable);
            ArgumentNullException.ThrowIfNull(state);

            if (toRebuild.Length == 0)
                return false;

            var rebuilder = rebuilderTable.GetRebuilder(RebuildColumns);
            if (rebuilder is null)
                return false;

            var changed = RebuildWith(rebuilder, rebuilderTableId, rebuilderTable, nextTimestamp, state);
            MarkRebuilt(rebuilderTableId);
            return changed;
        }

        /// <summary>
        /// The body of <see cref="ApplyRebuild"/>: picks the incremental or the full path and stages the rewrites.
        /// </summary>
        /// <remarks>
        /// A child context's layer (colored-contexts spec §7.3, R2-12) takes the incremental path whenever the rebuilder's
        /// delta is no larger than its row count (or 64), instead of egglog's rule, which requires more than 10 000 rows:
        /// a child's layers are small, and their full path would rewrite every own row on every pass. Refreshing a small
        /// layer's rebuild index is cheap, and the incremental path also gathers the rows written since the last rebuild,
        /// so both paths stage the same writes (<see cref="RebuildIncremental"/>). The root keeps egglog's rule; it pays one
        /// null test per table rebuild.
        /// </remarks>
        /// <returns>Whether anything was staged.</returns>
        bool RebuildWith(Rebuilder rebuilder, TableId rebuilderTableId, Table rebuilderTable, uint nextTimestamp, ExecutionState state)
        {
            if (rebuilder.HintColumn is { } hintColumn)
            {
                var toScan = subsetTracker.RecentUpdates(rebuilderTableId, rebuilderTable);
                var size = toScan.Size;
                var incremental = layer is not null
                    ? size <= Math.Max(data.Count, 64)
                    : ShouldRebuildIncrementally(size, data.Count, parallelism.ParallelizeRebuild(size));
                if (incremental)
                {
                    var handled = false;
                    var changed = false;
                    RebuildIncremental(rebuilderTableId, rebuilderTable, rebuilder, hintColumn, toScan.AsRef(), nextTimestamp, state, ref handled, ref changed);
                    if (handled)
                    {
                        lastRebuildIncremental = true;
                        toScan.Dispose();
                        return changed;
                    }
                }

                toScan.Dispose();
            }

            return RebuildFull(rebuilder, nextTimestamp, state);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// egglog's <c>refresh_rows_for_values</c> (core-relations/src/table/rebuild.rs:77-118): every live row whose
        /// rebuildable columns mention a dirty id is staged for removal and re-insertion with
        /// <paramref name="nextTimestamp"/> in the sort column, so after the next merge it is a fresh delta. Rows are
        /// staged in row order (egglog iterates a hash set).
        /// </para>
        /// <para>
        /// egglog finds the rows through its rebuild index. Until the indexes milestone implements
        /// <see cref="CollectRowsMentioning"/>, this scans the live rows, which finds the same rows.
        /// </para>
        /// </remarks>
        public override bool RefreshRowsForValues(ReadOnlySpan<uint> dirtyIds, uint nextTimestamp)
        {
            if (dirtyIds.IsEmpty || toRebuild.Length == 0)
                return false;

            var dirty = new HashSet<uint>(dirtyIds.Length);
            foreach (var id in dirtyIds)
                dirty.Add(id);

            var candidates = new List<int>();
            var handled = false;
            CollectRowsMentioning(dirty, candidates, ref handled);
            if (handled == false)
                CollectRowsMentioningByScan(dirty, candidates);

            if (candidates.Count == 0)
                return false;

            candidates.Sort();
            var writes = NewBuffer();
            var refreshed = new uint[Arity];
            var changed = false;
            var previous = -1;
            foreach (var row in candidates)
            {
                if (row == previous || data.IsStale(row))
                    continue;

                previous = row;
                var current = data.GetRow(row);
                writes.StageRemove(current[..keyCount]);
                current.CopyTo(refreshed);
                if (sortColumn >= 0)
                    refreshed[sortColumn] = nextTimestamp;

                writes.StageInsert(refreshed);
                changed = true;
            }

            writes.Flush();
            return changed;
        }

        /// <summary>
        /// INCREMENTAL REBUILD HOOK. The indexes milestone implements this partial method (egglog's
        /// <c>rebuild_incremental</c>, core-relations/src/table/rebuild.rs:120-195) in its own file: refresh the rebuild
        /// index (value to rows over <see cref="RebuildColumns"/>), project <paramref name="hintColumn"/> of
        /// <paramref name="toScan"/> of the providing table to get the displaced ids, have
        /// <see cref="Rebuilder.RebuildSubset"/> rewrite the rows that mention each one, and stage remove-and-insert
        /// for the changed rows exactly as <see cref="StageRebuiltRows"/> does, reporting only real changes (egglog's
        /// serial path reports any non-empty subset, rebuild.rs:182; map §8.1 row 17). Set <paramref name="handled"/>
        /// to true when it ran. Without an implementation the call is compiled away and the full path runs.
        /// </summary>
        /// <param name="rebuilderTableId">The providing table's id, which keys its rebuild mark (<see cref="MarkRebuilt"/>).</param>
        /// <param name="rebuilderTable">The providing table.</param>
        /// <param name="rebuilder">The rebuilder.</param>
        /// <param name="hintColumn">The providing table's column naming changed values.</param>
        /// <param name="toScan">The providing table's rows appended since the last rebuild.</param>
        /// <param name="nextTimestamp">The timestamp rewritten rows get.</param>
        /// <param name="state">The execution state.</param>
        /// <param name="handled">Set to true if the incremental rebuild ran.</param>
        /// <param name="changed">Set to whether it staged anything.</param>
        partial void RebuildIncremental(TableId rebuilderTableId, Table rebuilderTable, Rebuilder rebuilder, ColumnId hintColumn, SubsetRef toScan, uint nextTimestamp, ExecutionState state, ref bool handled, ref bool changed);

        /// <summary>
        /// REBUILD INDEX HOOK. The indexes milestone implements this partial method to add to
        /// <paramref name="candidates"/> the rows its rebuild index lists under each value of <paramref name="values"/>
        /// (stale rows and duplicates allowed), and sets <paramref name="handled"/>. Without an implementation the call
        /// is compiled away and <see cref="RefreshRowsForValues"/> scans the table instead.
        /// </summary>
        /// <param name="values">The values sought in the rebuildable columns.</param>
        /// <param name="candidates">Receives row ids.</param>
        /// <param name="handled">Set to true if the index answered.</param>
        partial void CollectRowsMentioning(HashSet<uint> values, List<int> candidates, ref bool handled);

        /// <summary>
        /// Gets whether a rebuild should take the incremental path: egglog's <c>incremental_rebuild</c>
        /// (core-relations/src/table/rebuild.rs:272-278).
        /// </summary>
        /// <param name="deltaSize">The number of new rows in the providing table.</param>
        /// <param name="tableSize">This table's physical row count.</param>
        /// <param name="parallel">Whether the rebuild would run in parallel.</param>
        /// <returns><c>true</c> for the incremental path.</returns>
        internal static bool ShouldRebuildIncrementally(int deltaSize, int tableSize, bool parallel)
        {
            return parallel
                ? tableSize > 10_000 && (long)deltaSize * 8192 <= tableSize
                : tableSize > 10_000 && (long)deltaSize * 8 <= tableSize;
        }

        /// <summary>
        /// Rebuilds by scanning every physical row in chunks of <see cref="RebuildStepSize"/>: egglog's
        /// <c>rebuild_nonincremental</c> (core-relations/src/table/rebuild.rs:197-269). Above
        /// <see cref="ParallelHeuristics.RebuildThreshold"/> rows the chunks run in parallel, each task with its own
        /// mutation buffer and execution-state clone.
        /// </summary>
        /// <returns>Whether any row was staged.</returns>
        bool RebuildFull(Rebuilder rebuilder, uint nextTimestamp, ExecutionState state) => RebuildRange(rebuilder, 0, data.Count, nextTimestamp, state);

        /// <summary>
        /// Rebuilds the physical rows <c>[first, end)</c> in chunks of <see cref="RebuildStepSize"/>, in parallel above
        /// <see cref="ParallelHeuristics.RebuildThreshold"/> rows: the body of <see cref="RebuildFull"/> (the whole table)
        /// and of <see cref="SettleAppends"/> (the rows written since the last rebuild).
        /// </summary>
        /// <returns>Whether any row was staged.</returns>
        bool RebuildRange(Rebuilder rebuilder, int first, int end, uint nextTimestamp, ExecutionState state)
        {
            lastRebuildIncremental = false;
            lastRebuildRows = end - first;
            if (parallelism.ParallelizeRebuild(end - first))
                return RebuildFullParallel(rebuilder, nextTimestamp, state, first, end);

            var rebuilt = new TaggedRowBuffer(Arity);
            for (var start = first; start < end; start += RebuildStepSize)
                rebuilder.RebuildBuffer(data, start, Math.Min(start + RebuildStepSize, end), rebuilt, state);

            var changed = false;
            if (rebuilt.IsEmpty == false)
            {
                var writes = NewBuffer();
                changed = StageRebuiltRows(rebuilt, writes, nextTimestamp);
                writes.Flush();
            }

            rebuilt.Dispose();
            return changed;
        }

        /// <summary>
        /// The parallel arm of <see cref="RebuildFull"/>: egglog's <c>into_par_iter().step_by(STEP_SIZE).fold</c>.
        /// </summary>
        bool RebuildFullParallel(Rebuilder rebuilder, uint nextTimestamp, ExecutionState state, int first, int end)
        {
            var chunks = (end - first + RebuildStepSize - 1) / RebuildStepSize;
            var changed = 0;
            var stateChanged = 0;
            Parallel.For(
                0,
                chunks,
                parallelism.Options,
                () => (Writes: NewBuffer(), Rebuilt: new TaggedRowBuffer(Arity), State: state.Clone(), Changed: false),
                (chunk, _, local) =>
                {
                    var start = first + chunk * RebuildStepSize;
                    rebuilder.RebuildBuffer(data, start, Math.Min(start + RebuildStepSize, end), local.Rebuilt, local.State);
                    local.Changed |= StageRebuiltRows(local.Rebuilt, local.Writes, nextTimestamp);
                    local.Rebuilt.Clear();
                    return local;
                },
                local =>
                {
                    local.Writes.Flush();
                    local.Rebuilt.Dispose();
                    local.State.Flush();
                    if (local.Changed)
                        Interlocked.Exchange(ref changed, 1);

                    if (local.State.Changed)
                        Interlocked.Exchange(ref stateChanged, 1);
                });

            if (stateChanged != 0)
                state.Changed = true;

            return changed != 0;
        }

        /// <summary>
        /// Settles the rows written since the table last rebuilt against <paramref name="unionFind"/> (colored-contexts
        /// spec §7.6, X38): rewrites every live row of <c>[FirstRowSinceRebuild, Rows.Count)</c> that names a
        /// non-canonical id, as egglog's rebuild rewrites a row (stage the removal of its key and the canonical row stamped
        /// <paramref name="timestamp"/>), and moves the table's rebuild mark to its end. Called on the root, before it is
        /// shared (<see cref="Context.Settle"/>), through <see cref="SettleTableOperation"/>, so the database notifies a
        /// table that staged anything and the next merge applies the rewrites.
        /// </summary>
        /// <remarks>
        /// Rows before the mark were canonical at the last rebuild and are kept canonical by the journal-driven rebuilds
        /// since; rows after it are exactly the rows a raw write could have left stale. A table without rebuild columns has
        /// nothing to settle. Moving the mark is sound for the next incremental rebuild: every row before the new mark is
        /// canonical now, and ids displaced later are in that rebuild's delta. Cost: O(rows written since the last rebuild).
        /// </remarks>
        /// <param name="unionFind">The union-find's table id, which keys the rebuild mark.</param>
        /// <param name="unionFindTable">The union-find table, which provides the rebuilder.</param>
        /// <param name="timestamp">The settle's epoch, which rewritten rows get.</param>
        /// <param name="state">The execution state the rebuilder runs in.</param>
        /// <returns>Whether anything was staged.</returns>
        internal bool SettleAppends(TableId unionFind, Table unionFindTable, uint timestamp, ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(unionFindTable);
            ArgumentNullException.ThrowIfNull(state);
            if (toRebuild.Length == 0)
                return false;

            var first = FirstRowSinceRebuild(unionFind);
            var end = data.Count;
            var changed = false;
            if (first < end && unionFindTable.GetRebuilder(RebuildColumns) is { } rebuilder)
                changed = RebuildRange(rebuilder, first, end, timestamp, state);

            MarkRebuilt(unionFind);
            return changed;
        }

        /// <summary>
        /// Stages, for every live rewritten row of <paramref name="rebuilt"/>, the removal of the key its source row has
        /// now and the insertion of the rewritten row with <paramref name="nextTimestamp"/> in the sort column:
        /// egglog's <c>insert_row!</c> loops (core-relations/src/table/rebuild.rs:20-31, 184-192, 257-266).
        /// </summary>
        /// <returns>Whether anything was staged.</returns>
        bool StageRebuiltRows(TaggedRowBuffer rebuilt, MutationBuffer writes, uint nextTimestamp)
        {
            var changed = false;
            foreach (var (rowId, row) in rebuilt.EnumerateNonStale())
            {
                if (data.IsStale(rowId) == false)
                    writes.StageRemove(data.GetRow(rowId)[..keyCount]);

                if (sortColumn >= 0)
                    row[sortColumn] = nextTimestamp;

                writes.StageInsert(row);
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Adds to <paramref name="candidates"/> every live row whose rebuildable columns hold one of
        /// <paramref name="values"/>, in row order. O(physical rows × rebuildable columns).
        /// </summary>
        void CollectRowsMentioningByScan(HashSet<uint> values, List<int> candidates)
        {
            var cells = data.Data;
            var count = data.Count;
            var arity = Arity;
            for (var row = 0; row < count; row++)
            {
                var offset = row * arity;
                if (cells[offset] == Value.StaleRep)
                    continue;

                foreach (var column in toRebuild)
                {
                    if (values.Contains(cells[offset + column]))
                    {
                        candidates.Add(row);
                        break;
                    }
                }
            }
        }

    }

}
