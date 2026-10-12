using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class ContextTables
    {

        /// <summary>
        /// The cursor into each ancestor, by depth; only depths in <c>[settled, Depth)</c> have one.
        /// </summary>
        readonly AncestorCursor?[] cursors;

        /// <summary>
        /// The number of leading ancestors the context has read in full: frozen ancestors that cannot change any more,
        /// whose cursors are dropped (§6.2).
        /// </summary>
        int settled;

        /// <summary>
        /// Gets whether some ancestor that is not settled changed since the context last caught up with it: its change
        /// stamp moved (§7.2; the root's is its database's <see cref="Database.ChangeStamp"/>). O(unsettled ancestors).
        /// </summary>
        public bool NeedsCatchUp
        {
            get
            {
                for (var a = settled; a < depth; a++)
                    if (chain[a].Database.ChangeStamp != cursors[a]!.Stamp)
                        return true;

                return false;
            }
        }

        /// <summary>
        /// Gets the number of leading ancestors the context has read in full (frozen, cursors dropped).
        /// </summary>
        internal int SettledDepth => settled;

        /// <summary>
        /// Gets the cursor into the ancestor at depth <paramref name="depthOfAncestor"/>, or <c>null</c> when it is settled.
        /// </summary>
        /// <param name="depthOfAncestor">An ancestor depth.</param>
        /// <returns>The cursor, or <c>null</c>.</returns>
        internal AncestorCursor? CursorAt(int depthOfAncestor) => cursors[depthOfAncestor];

        /// <summary>
        /// Interim (M9.3, §17): gets whether an ancestor that is not settled appended or replaced rows of a shared table
        /// since the context last caught up with it (the version of its layer moved), which only the row catch-up of M9.4
        /// (<c>CatchUpRows</c>, §7.2) can bring into the context's view. O(tables × changed ancestors).
        /// </summary>
        /// <remarks>
        /// A layer whose version did not move holds the same rows, except for tombstones, which a version does not count.
        /// An ancestor with live descendants tombstones a row without appending one only when its own rebuild folds a
        /// rewritten row into a row that already holds the result: every retraction is refused there (decision 1, I22,
        /// at every entry point, <see cref="Database.NewBuffer"/> included), compaction is gated (§6.8), and a replaced row
        /// reappears as an append (§7.2). Such a fold follows a union, which reaches the context as an import, and
        /// <see cref="Context.Rebuild"/> refuses an import whenever the context holds rows it could make stale
        /// (<see cref="OwnRowsMention"/>). Otherwise the context sees the ancestor's tombstones as they are, which is
        /// what a copy sees.
        /// </remarks>
        /// <returns><c>true</c> if some ancestor's rows changed.</returns>
        internal bool AncestorRowsChanged()
        {
            for (var a = settled; a < depth; a++)
            {
                var cursor = cursors[a]!;
                if (chain[a].Database.ChangeStamp == cursor.Stamp)
                    continue;

                foreach (var table in schema.FunctionTables)
                {
                    if (schema[table] is not { IsContextPrivate: false })
                        continue;

                    var current = AncestorLayer(a, table)?.Version ?? default;
                    if (current != cursor.RowsOrDefault(table))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Interim (M9.3, §17): gets whether a live row the context holds itself names one of <paramref name="ids"/> in an
        /// id column: ids that stop being canonical in the context, so the row would need the rewrite that only step 3 of
        /// a child's rebuild pass (§7.3, M9.4) does. Rows the context hides are invisible and need none.
        /// </summary>
        /// <remarks>
        /// With <paramref name="unknownIds"/> the ids are not known before the rebuild runs (unions staged and not merged,
        /// or a re-key that may move the canonical of a class the context merged, §5.6), and any live own row of a table
        /// with id columns counts. Cost: O(own rows × id columns), the context's own state only.
        /// </remarks>
        /// <param name="ids">The ids known to stop being canonical.</param>
        /// <param name="unknownIds">Whether further ids may stop being canonical.</param>
        /// <returns><c>true</c> if such a row exists.</returns>
        internal bool OwnRowsMention(ReadOnlySpan<uint> ids, bool unknownIds)
        {
            HashSet<uint>? set = null;
            var current = Volatile.Read(ref slots);
            foreach (var layer in current)
            {
                if (layer is null || layer.Own.Count == 0)
                    continue;

                var columns = layer.Schema.RebuildColumns;
                if (columns.Length == 0)
                    continue;

                if (unknownIds)
                    return true;

                if (ids.IsEmpty)
                    return false;

                if (set is null)
                {
                    set = new HashSet<uint>();
                    foreach (var id in ids)
                        set.Add(id);
                }

                var rows = layer.Own.Rows;
                for (var r = 0; r < rows.Count; r++)
                {
                    if (rows.IsStale(r))
                        continue;

                    var cells = rows.GetRow(r);
                    foreach (var column in columns)
                        if (set.Contains(cells[column.Rep]))
                            return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Interim (M9.3, §17): gets whether some ancestor row the context sees names one of <paramref name="losers"/>,
        /// ids that stop being canonical in the context while they stay canonical in its parent: such a row must be hidden
        /// and copied canonically, which only step 4 of a child's rebuild pass (§7.3, M9.4) does.
        /// </summary>
        /// <remarks>
        /// A frozen ancestor layer is probed through its rebuild index, built at its freeze (§8.1); a writable one, whose
        /// rebuild index belongs to its owner, counts as soon as it holds a live row. With
        /// <paramref name="unknownLosers"/> the losers are not known before the rebuild runs (unions staged and not merged,
        /// or a re-key that may move the canonical of a class the context merged, §5.6), and any visible layer of a shared
        /// table with id columns that holds a live row counts. O(tables × depth × losers) index probes.
        /// </remarks>
        /// <param name="losers">The own losers known now; ids local to the context are left out (no ancestor row names
        /// them, I10).</param>
        /// <param name="unknownLosers">Whether further losers may appear.</param>
        /// <returns><c>true</c> if such a row exists.</returns>
        internal bool AncestorRowsMention(ReadOnlySpan<uint> losers, bool unknownLosers)
        {
            foreach (var table in schema.FunctionTables)
            {
                if (schema[table] is not { IsContextPrivate: false } s || s.RebuildColumns.Length == 0)
                    continue;

                for (var j = ClearedFloor(table); j < depth; j++)
                {
                    if (AncestorLayer(j, table) is not { } layer || layer.Count == 0)
                        continue;

                    if (unknownLosers || layer.IsShared == false || layer.RebuildIndex is not { } index || index.NeedsRefresh(layer))
                        return true;

                    foreach (var loser in losers)
                    {
                        if (index.Data.TryGetSubset(loser, out var rows) == false)
                            continue;

                        foreach (var row in rows)
                            if (layer.Rows.IsStale(row) == false && HiddenBetween(table, j, row) == false)
                                return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Moves every cursor to its ancestor's current state, after a rebuild loop, while no ancestor can change (they are
        /// frozen or share the domain); a frozen ancestor read in full becomes settled and drops its cursor. Interim
        /// (M9.3): <see cref="AncestorRowsChanged"/> has established that no layer version moved, so only stamps change.
        /// </summary>
        /// <returns>Whether any cursor moved.</returns>
        internal bool CommitCursors()
        {
            var moved = false;
            for (var a = settled; a < depth; a++)
            {
                var cursor = cursors[a]!;
                var stamp = chain[a].Database.ChangeStamp;
                if (stamp == cursor.Stamp && cursor.TableCount >= schema.TableCount)
                    continue;

                foreach (var table in schema.FunctionTables)
                    cursor.SetPendingRows(table, AncestorLayer(a, table)?.Version ?? default);

                cursor.SetPendingStamp(stamp);
                moved |= cursor.CommitPending();
            }

            while (settled < depth && chain[settled].IsFrozen)
                cursors[settled++] = null;

            return moved;
        }

        /// <summary>
        /// Takes the creation cursors: the leading frozen ancestors are settled (the context starts from their final
        /// state), and every other ancestor gets a cursor at its current layer versions and change stamp. Called by the
        /// constructor, before <see cref="Context.CreatedAt"/> is read.
        /// </summary>
        void InitializeCursors()
        {
            while (settled < depth && chain[settled].IsFrozen)
                settled++;

            var count = schema.TableCount;
            for (var a = settled; a < depth; a++)
            {
                var rows = new TableVersion[count];
                foreach (var table in schema.FunctionTables)
                    if (table.Index < count)
                        rows[table.Index] = AncestorLayer(a, table)?.Version ?? default;

                cursors[a] = new AncestorCursor(rows, chain[a].Database.ChangeStamp);
            }
        }

    }

}
