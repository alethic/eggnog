using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    internal sealed partial class SortedWritesTable
    {

        /// <summary>
        /// The rebuild index: every row listed under each distinct value of its rebuildable columns. egglog's
        /// <c>rebuild_index: Index&lt;ColumnIndex&gt;</c> (core-relations/src/table/mod.rs:149). Created on first use;
        /// a clone starts without one, as egglog resets it when cloning (mod.rs:167).
        /// </summary>
        Index<ColumnIndex>? rebuildIndex;

        /// <summary>
        /// Gets the rebuild index as last refreshed, or <c>null</c> if no rebuild or refresh has needed it yet. For
        /// diagnostics and tests.
        /// </summary>
        internal Index<ColumnIndex>? RebuildIndex => rebuildIndex;

        /// <summary>
        /// Brings the rebuild index up to the table's version, creating it on first use: egglog's
        /// <c>refresh_rebuild_index</c> (core-relations/src/table/rebuild.rs:34-43). O(rows appended since the last
        /// refresh), or O(rows) after a new generation.
        /// </summary>
        /// <returns>The index.</returns>
        /// <remarks>
        /// Requires rebuild columns (an index needs a key column). Exclusive: the table's owner calls it while it
        /// rebuilds or freezes, and a child context calls it on an ancestor that is idle in the child's thread domain
        /// (<see cref="SharedRebuildIndex"/>).
        /// </remarks>
        internal Index<ColumnIndex> RefreshRebuildIndex()
        {
            var index = rebuildIndex ??= new Index<ColumnIndex>(RebuildColumns, new ColumnIndex(parallelism));
            index.Refresh(this);
            return index;
        }

        /// <summary>
        /// Gets the rebuild index as descendants read it: <c>null</c> for a table without rebuild columns (no id can be
        /// mentioned, and an index needs a key column); for a frozen table, the index its freeze built, which is
        /// current and shared lock-free by any number of concurrent descendants; otherwise the index refreshed now
        /// (<see cref="RefreshRebuildIndex"/>), which a child context does only for an ancestor that is not frozen and
        /// so shares its thread domain and is idle.
        /// </summary>
        /// <returns>The index, or <c>null</c>.</returns>
        internal Index<ColumnIndex>? SharedRebuildIndex()
        {
            if (toRebuild.Length == 0)
                return null;

            if (isShared)
            {
                Debug.Assert(rebuildIndex is not null && rebuildIndex.NeedsRefresh(this) == false, "a frozen table's rebuild index is current");
                return rebuildIndex;
            }

            return RefreshRebuildIndex();
        }

        /// <summary>
        /// Adds to <paramref name="candidates"/> the rows the rebuild index lists under each of <paramref name="values"/>
        /// (stale rows and repeats allowed), for <see cref="RefreshRowsForValues"/>: egglog's candidate collection
        /// (core-relations/src/table/rebuild.rs:81-93), which probes the rebuild index for every dirty id. O(dirty ids +
        /// rows found) after the refresh.
        /// </summary>
        /// <param name="values">The dirty ids.</param>
        /// <param name="candidates">Receives the rows listed under them.</param>
        void CollectRowsMentioning(HashSet<uint> values, List<int> candidates)
        {
            var index = RefreshRebuildIndex().Data;
            foreach (var value in values)
            {
                if (index.TryGetSubset(value, out var rows))
                {
                    foreach (var row in rows)
                        candidates.Add(row);
                }
            }
        }

        /// <summary>
        /// The incremental path of <see cref="ApplyRebuild"/>: egglog's <c>rebuild_incremental</c>
        /// (core-relations/src/table/rebuild.rs:120-195). Refreshes the rebuild index, reads the ids the providing table
        /// displaced since this table's last rebuild against it (its <paramref name="hintColumn"/> over
        /// <paramref name="toScan"/>), gathers the rows the index lists under them and the rows appended since that
        /// rebuild, and has the rebuilder rewrite those rows (<see cref="RebuildRows"/>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// egglog gathers only the rows that mention an id displaced since this table's last rebuild (rebuild.rs:52-66,
        /// 120-195). A row written after that rebuild that mentions an id displaced before it is then never rewritten,
        /// while the full path, taken for tables of at most 10000 rows, rewrites it, so the result would depend on the
        /// table's size. egglog-bridge happens to write only ids canonical as of the last rebuild; this table does not
        /// rely on it. The rows appended since the last rebuild against the same rebuilder
        /// (<see cref="FirstRowSinceRebuild"/>; all of them after a new generation) are gathered too, which costs
        /// O(rows written since), at most what the full path costs; rows older than that were canonical as of that
        /// rebuild, so only ids the rebuilder displaced since can make them stale. The mark is kept per rebuilder, like
        /// the <see cref="SubsetTracker"/> position that names those ids: a rebuild against another union-find moves
        /// neither. The rewrite goes parallel by the number of rows gathered, the work it does, where egglog uses the
        /// number of displaced ids.
        /// </para>
        /// <para>
        /// egglog rewrites each displaced id's rows separately, so a row mentioning two displaced ids (or one id in two
        /// columns, which its multi-column index lists twice) is rewritten and staged twice. A second staged insert of
        /// the same row goes through the merge function, which double-counts with a merge that is not idempotent, and
        /// the staging order follows the displaced ids. Here the rows are gathered, sorted and deduplicated first, so
        /// each is rewritten once and staged in row order, exactly as the full scan stages them: the two paths give the
        /// same writes.
        /// </para>
        /// <para>
        /// Reports whether anything was staged; egglog's serial path reports a change whenever some id's subset is
        /// nonempty (rebuild.rs:182).
        /// </para>
        /// </remarks>
        /// <param name="rebuilderTableId">The providing table's id, which keys its rebuild mark (<see cref="MarkRebuilt"/>).</param>
        /// <param name="rebuilderTable">The providing table.</param>
        /// <param name="rebuilder">The rebuilder.</param>
        /// <param name="hintColumn">The providing table's column naming changed values.</param>
        /// <param name="toScan">The providing table's rows appended since the last rebuild.</param>
        /// <param name="nextTimestamp">The timestamp rewritten rows get.</param>
        /// <param name="state">The execution state.</param>
        /// <returns>Whether anything was staged.</returns>
        bool RebuildIncremental(TableId rebuilderTableId, Table rebuilderTable, Rebuilder rebuilder, ColumnId hintColumn, SubsetRef toScan, uint nextTimestamp, ExecutionState state)
        {
            var index = RefreshRebuildIndex().Data;
            var gather = new ColumnIndexGatherSink(index);
            rebuilderTable.ForEachColumn(toScan, hintColumn, ref gather);
            gather.AppendRange(FirstRowSinceRebuild(rebuilderTableId), data.Count);
            var count = gather.SortUnique();
            lastRebuildRows = count;
            var changed = count > 0 && RebuildRows(rebuilder, gather.Rows, count, nextTimestamp, state, parallelism.ParallelizeRebuild(count));
            gather.Release();
            return changed;
        }

        /// <summary>
        /// Rewrites the live rows among <c>rows[0..count)</c> with <paramref name="rebuilder"/> and stages, for each row
        /// it changes, the removal of the row's key and the insertion of the rewritten row stamped with
        /// <paramref name="nextTimestamp"/>. The body of egglog's <c>rebuild_incremental</c> after the candidate rows are
        /// known: serially one <see cref="Rebuilder.RebuildSubset"/> over all of them; in parallel (egglog's
        /// <c>par_iter().fold</c>) chunks of <see cref="RebuildStepSize"/> rows, each task with its own mutation buffer
        /// and execution-state clone, as the full rebuild does.
        /// </summary>
        /// <param name="rebuilder">The rebuilder.</param>
        /// <param name="rows">Row ids of this table, ascending and distinct; stale rows are skipped.</param>
        /// <param name="count">The number of row ids.</param>
        /// <param name="nextTimestamp">The timestamp rewritten rows get.</param>
        /// <param name="state">The execution state.</param>
        /// <param name="parallel">Whether to rewrite in parallel.</param>
        /// <returns>Whether anything was staged.</returns>
        internal bool RebuildRows(Rebuilder rebuilder, int[] rows, int count, uint nextTimestamp, ExecutionState state, bool parallel)
        {
            ArgumentNullException.ThrowIfNull(rebuilder);
            ArgumentNullException.ThrowIfNull(rows);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)count, (uint)rows.Length, nameof(count));

            if (count == 0)
                return false;

            // Checks the bounds, and the order in debug builds.
            var subset = SubsetRef.Sparse(rows, 0, count);
            if (parallel)
                return RebuildRowsParallel(rebuilder, rows, count, nextTimestamp, state);

            var rebuilt = new TaggedRowBuffer(Arity);
            rebuilder.RebuildSubset(this, subset, rebuilt, state);
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
        /// The parallel arm of <see cref="RebuildRows"/>.
        /// </summary>
        bool RebuildRowsParallel(Rebuilder rebuilder, int[] rows, int count, uint nextTimestamp, ExecutionState state)
        {
            var chunks = (count + RebuildStepSize - 1) / RebuildStepSize;
            var changed = 0;
            var stateChanged = 0;
            Parallel.For(
                0,
                chunks,
                parallelism.Options,
                () => (Writes: NewBuffer(), Rebuilt: new TaggedRowBuffer(Arity), State: state.Clone(), Changed: false),
                (chunk, _, local) =>
                {
                    var start = chunk * RebuildStepSize;
                    var subset = SubsetRef.FromTrusted(rows, start, Math.Min(start + RebuildStepSize, count));
                    rebuilder.RebuildSubset(this, subset, local.Rebuilt, local.State);
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

    }

}
