using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class ContextTables
    {

        /// <summary>
        /// The ancestor rows copied by the current catch-up or hide-and-copy step, with the layer each lies in and why
        /// it is hidden: hidden by <see cref="ApplyHides"/> once every copy is staged. Domain thread only; reused.
        /// </summary>
        List<(TableId Table, int Layer, int Row, HideReason Reason)>? hides;

        /// <summary>
        /// The own losers of the current pass. Domain thread only; reused.
        /// </summary>
        List<uint>? losers;

        /// <summary>
        /// The own layers a pass rewrites. Domain thread only; reused.
        /// </summary>
        List<TableId>? ownRebuildTables;

        /// <summary>
        /// The row a copy is prepared in. Domain thread only; reused.
        /// </summary>
        uint[]? copyScratch;

        /// <summary>
        /// One rebuild pass of the child at <paramref name="epoch"/>: rewrites the context's own rows that name an id
        /// displaced since the last pass, hides and copies canonically the ancestor rows that name one of its own new
        /// losers, then merges. In the first pass of a rebuild these steps follow the catch-up
        /// (<see cref="Context.Rebuild"/>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The delta is the journal's rows since the context's journal mark, which the pass consumes: the imports and
        /// re-key losers of the catch-up and the context's own losers.
        /// </para>
        /// <para>
        /// Rewrite: egglog's incremental rebuild of every adopted own layer with rebuild columns against the context's
        /// union-find table (<see cref="SortedWritesTable.ApplyRebuild"/>, which reads the journal suffix through its
        /// <see cref="SubsetTracker"/> and also gathers the rows written since its last rebuild), through
        /// <see cref="Database.RunWithTakenOut{TOperation}"/>, so the database notifies every layer whose rebuild
        /// staged a rewrite and records failures. Own rows were canonical when appended, so the step is skipped when no
        /// id was displaced since the last pass.
        /// </para>
        /// <para>
        /// Hide and copy, for losers only (a union hides and copies the ancestor rows that name its loser, never those
        /// that name its winner):
        /// for every shared table with rebuild columns and every ancestor layer the context sees, the rows its rebuild
        /// index lists under an own loser (<see cref="SortedWritesTable.SharedRebuildIndex"/>,
        /// <see cref="LayerStatistics.AncestorRowsExamined"/>), live and hidden at no level between, are copied
        /// canonically and hidden (<see cref="HideReason.Mentions"/>). Imports need no such step: the ancestor rows
        /// naming them are already invisible at the parent. Never a scan: O(depth × own losers) probes plus their uses.
        /// </para>
        /// <para>
        /// Merge: <see cref="Database.MergeAll"/>, which merges the catch-up's copies, the rewrites and the
        /// hide-and-copy step's copies in stratum order, folds rows that came to share a key through their merge
        /// functions (congruence, which may stage unions for the next pass), and seals the hides in its epilogue.
        /// </para>
        /// </remarks>
        /// <param name="epoch">The pass's epoch.</param>
        /// <returns>Whether the pass changed anything: the merge changed rows or staged writes, or a row was hidden.</returns>
        /// <exception cref="InvalidOperationException">A copy would create the layer of a table whose merge is not a
        /// lattice merge, which only the root may write; the context is then faulted.</exception>
        internal bool RebuildPass(uint epoch)
        {
            if (owner.IsContradicted)
                return false;

            var journal = owner.Journal!;
            var from = owner.JournalMark;
            var end = journal.Count;
            owner.JournalMark = end;

            // A layer created by a search that no merge round followed is rewritten with the others.
            AdoptDeferred();

            // The own losers. A loser local to the context is left out: no ancestor row names one.
            var ownLosers = losers ??= new List<uint>();
            ownLosers.Clear();
            var ids = journal.Ids;
            var local = owner.IdBlocks!;
            for (var i = from; i < end; i++)
                if (owner.IsOwnDisplacement(ids[i]) && local.Contains(ids[i]) == false)
                    ownLosers.Add(ids[i]);

            // Rewrite the own rows that name an id displaced since the last pass.
            if (from < end)
            {
                var own = OwnRebuildTables();
                if (own.Length > 0)
                    owner.Database.RunWithTakenOut(schema.UnionFind, own, new RebuildTableOperation(schema.UnionFind, owner.UnionFindTable, epoch));
            }

            // Hide and copy the ancestor rows that name an own loser.
            ExecutionState? state = null;
            PendingHides();
            if (ownLosers.Count > 0)
                CopyRowsMentioningLosers(ref state, CollectionsMarshal.AsSpan(ownLosers), epoch);

            if (state is not null)
            {
                state.Flush();
                owner.Database.RecordFailure(state);
            }

            var hidden = ApplyHides();

            // Merge.
            return owner.Database.MergeAll(epoch) | hidden;
        }

        /// <summary>
        /// The hide-and-copy step of <see cref="RebuildPass"/>: stages a canonical copy of every visible ancestor row
        /// that names one of <paramref name="ownLosers"/>, and records it for hiding.
        /// </summary>
        void CopyRowsMentioningLosers(ref ExecutionState? state, ReadOnlySpan<uint> ownLosers, uint epoch)
        {
            foreach (var table in schema.FunctionTables)
            {
                if (schema[table] is not { IsContextPrivate: false } s || s.RebuildColumns.Length == 0)
                    continue;

                for (var j = ClearedFloor(table); j < depth; j++)
                {
                    if (AncestorLayer(j, table) is not { Count: > 0 } layer)
                        continue;

                    var gather = new ColumnIndexGatherSink(layer.SharedRebuildIndex()!.Data);
                    foreach (var loser in ownLosers)
                        gather.AddValue(loser);

                    var count = gather.SortUnique();
                    statistics.AncestorRowsExamined += count;
                    var rows = gather.Rows;
                    var data = layer.Rows.Data;
                    var arity = layer.Arity;
                    for (var i = 0; i < count; i++)
                    {
                        var r = rows[i];
                        if (data[r * arity] == Value.StaleRep || HiddenBetween(table, j, r))
                            continue;

                        StageCopy(ref state, s, layer, j, r, HideReason.Mentions, true, epoch);
                    }

                    gather.Release();
                }
            }
        }

        /// <summary>
        /// Stages a copy of row <paramref name="row"/> of the ancestor layer at depth <paramref name="layerDepth"/>
        /// into the context, through <paramref name="state"/> (created on first use at <paramref name="epoch"/>), and
        /// records the row for hiding: the copy's id cells are rewritten to their canonical ids in the context when
        /// <paramref name="canonicalize"/> is set, and its timestamp is the epoch. The merge prepares it again
        /// (canonical id cells, the merge's epoch), which changes nothing.
        /// </summary>
        /// <param name="state">The pass's state; created here on first use.</param>
        /// <param name="s">The table's schema.</param>
        /// <param name="layer">The ancestor layer's table.</param>
        /// <param name="layerDepth">The ancestor layer's depth.</param>
        /// <param name="row">The row id in that layer.</param>
        /// <param name="reason">Why the row is copied and hidden.</param>
        /// <param name="canonicalize">Whether to canonicalize the copy's id cells.</param>
        /// <param name="epoch">The pass's epoch.</param>
        void StageCopy(ref ExecutionState? state, LayeredTableSchema s, SortedWritesTable layer, int layerDepth, int row, HideReason reason, bool canonicalize, uint epoch)
        {
            state ??= owner.Database.CreateEngineState(epoch);
            var arity = s.Arity;
            var copy = copyScratch;
            if (copy is null || copy.Length < arity)
                copyScratch = copy = new uint[Math.Max(arity, 8)];

            Array.Copy(layer.Rows.Data, row * arity, copy, 0, arity);
            if (canonicalize)
            {
                var find = owner.UnionFind;
                foreach (var column in s.RebuildColumns)
                    copy[column.Rep] = find.Find(copy[column.Rep]);
            }

            if (s.SortColumn >= 0)
                copy[s.SortColumn] = epoch;

            state.StageInsert(s.Id, copy.AsSpan(0, arity));
            hides!.Add((s.Id, layerDepth, row, reason));
            statistics.RowsCopied++;
        }

        /// <summary>
        /// Hides every row recorded by <see cref="StageCopy"/> since <see cref="PendingHides"/>, after their copies are
        /// staged and flushed: a hide is never applied before the row carrying its information is pending. The hides
        /// are sealed by the merge of the same pass, or by <see cref="Context.Rebuild"/>'s epilogue.
        /// </summary>
        /// <returns>Whether any row was hidden.</returns>
        bool ApplyHides()
        {
            var pending = hides!;
            if (pending.Count == 0)
                return false;

            foreach (var (table, layer, row, reason) in pending)
            {
                // Each row is staged at most once per pass (a gather is deduplicated, catch-up reads each row once, and
                // the hide-and-copy step skips rows the catch-up hid), so the row cannot be hidden here already.
                var added = GetOrCreateLayer(table).Hide(layer, row, reason);
                Debug.Assert(added, "an ancestor row was copied twice in one pass");
            }

            pending.Clear();
            return true;
        }

        /// <summary>
        /// Gets the empty list <see cref="StageCopy"/> records hides in.
        /// </summary>
        List<(TableId Table, int Layer, int Row, HideReason Reason)> PendingHides()
        {
            var pending = hides ??= new List<(TableId, int, int, HideReason)>();
            pending.Clear();
            return pending;
        }

        /// <summary>
        /// Brings the rebuild index of every own layer of a shared table with rebuild columns up to date: the indexes a
        /// child's hide-and-copy step and lift reads probe (<see cref="SortedWritesTable.SharedRebuildIndex"/>). Called
        /// when a child is created from the context while it is not frozen (<see cref="Context.CreateChild"/>), so the
        /// child never builds a whole index on its own path. O(own layers) plus the rows appended since each index's
        /// last refresh. Domain thread only.
        /// </summary>
        internal void RefreshOwnRebuildIndexes()
        {
            foreach (var layer in Volatile.Read(ref slots))
                if (layer is { IsAdopted: true, Schema.IsContextPrivate: false } && layer.Schema.RebuildColumns.Length > 0)
                    layer.Own.SharedRebuildIndex();
        }

        /// <summary>
        /// Gets the adopted own layers with rebuild columns, in ascending table order: what a rebuild pass rewrites and
        /// the container refresh re-stamps. Valid until the next call.
        /// </summary>
        internal ReadOnlySpan<TableId> OwnRebuildTables()
        {
            var list = ownRebuildTables ??= new List<TableId>();
            list.Clear();
            foreach (var layer in Volatile.Read(ref slots))
                if (layer is { IsAdopted: true } && layer.Schema.RebuildColumns.Length > 0)
                    list.Add(layer.Id);

            return CollectionsMarshal.AsSpan(list);
        }

    }

}
