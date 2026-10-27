using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// The first step of a child's catch-up: re-keys the child's union-find over the displacements its parent made
        /// since the child last caught up, through one cursor into the parent's displaced ids, journaling at
        /// <paramref name="epoch"/> every id that stops being canonical in the child.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For each new parent displacement <c>d</c>, in the parent's row order, the child computes <c>d</c>'s final
        /// canonical in the parent and re-keys its overlay (<see cref="UnionFindOverlay.Rekey"/>); the ids the re-key
        /// reports lost are journaled unless they already are. The parent's displaced ids re-export every displacement
        /// visible at the parent, its own imports included, so one cursor suffices. Afterwards every overlay member is
        /// canonical in the parent again, and the journal holds every id that stopped being canonical in the child
        /// since its creation.
        /// </para>
        /// <para>
        /// Cost: O(1) expected per parent displacement plus the sizes of the classes involved; nothing when the child is
        /// current, which is always the case under a frozen parent after the first catch-up. Called by
        /// <see cref="Rebuild"/>'s first pass, inside the domain, after the parent was rebuilt or with a frozen parent.
        /// </para>
        /// <para>
        /// Unions staged in the child and not merged yet would be merged after the imports, possibly with an older
        /// timestamp than they carry, so they are refused when there is something to catch up with, and only then:
        /// every caller's staged unions are merged by <see cref="Rebuild"/> right after, and a rebuild with nothing to
        /// catch up with does not fail.
        /// </para>
        /// </remarks>
        /// <param name="epoch">The rebuild pass's epoch, stamped on every journaled id.</param>
        /// <returns>The journal's row count before the catch-up: the first row it appended.</returns>
        /// <exception cref="InvalidOperationException">Unions are staged in the child and not merged while the parent
        /// has new displacements.</exception>
        int CatchUpUnionFind(uint epoch)
        {
            Debug.Assert(parent is not null && overlay is not null && journal is not null && overlayTable is not null);
            Debug.Assert(parent.IsFrozen || parent.IsClean, "a child catches up only with a frozen or clean parent");

            var from = journal.Count;
            var end = parent.DisplacedCount;
            if (unionFindCursor == end)
                return from;

            if (overlayTable.HasPendingUnions)
                throw new InvalidOperationException("Unions staged in a context must be merged before it catches up with its parent.");

            var ids = parent.DisplacedIds;
            Span<uint> lost = stackalloc uint[2];
            for (var i = unionFindCursor; i < end; i++)
            {
                var d = ids[i];
                Debug.Assert(idBlocks!.Contains(d) == false, "a parent displaced an id local to its child");

                // The final canonical of d in the parent: within a batch, chains d → p1 → p2 resolve to p2.
                var p = parentFinder!.Find(d);
                Debug.Assert(p < d, "a displaced id's canonical is smaller than it");
                var n = overlay.Rekey(d, p, lost);
                for (var j = 0; j < n; j++)
                {
                    var id = lost[j];
                    if (journal.Contains(id))
                        continue;

                    journal.Append(id, epoch);
                    if (id == d)
                        Interlocked.Increment(ref importedDisplacements);
                    else
                        Interlocked.Increment(ref rekeyedMembers);
                }
            }

            unionFindCursor = end;
            return from;
        }

        /// <summary>
        /// The container step of each pass of a child's rebuild, before its tables' pass: the container pass over its
        /// container layers (<see cref="ContainerLayers.RebuildPass"/>), then, when the pass changed something, the
        /// merge of the unions it staged into the union-find, so that the tables' pass that follows rewrites the rows
        /// naming the containers merged away, as egglog's rebuild loop merges the container rebuild's unions before the
        /// tables rebuild (egglog-bridge/src/lib.rs:666-668); then the re-stamp, at <paramref name="epoch"/>, of the
        /// rows of the child's own layers that name a dirty container, so the child's semi-naive evaluation sees them
        /// as new, as egglog's <c>refresh_rows_for_values</c> does at the root; and, while the child has live
        /// descendants, the dirty ids the pass found join the Dirty stream of its container log. With
        /// <paramref name="settle"/>, all of it repeats, at the same epoch, until a container pass changes nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The dirty ids are the containers the pass rebuilt in place, and <paramref name="inherited"/>, the ones the
        /// unsettled ancestors rebuilt in place since the child last looked, which the first pass reads from their
        /// container logs (<see cref="ContextTables.ReadDirty"/>), closed over the child's view. Rows of an ancestor
        /// that name an inherited id were re-stamped by that ancestor and arrive as appends; those that name an id the
        /// pass found are re-stamped by the tables' pass, which hides and copies them
        /// (<see cref="HideReason.Refreshed"/>).
        /// </para>
        /// <para>
        /// The re-stamp runs <see cref="RefreshTableOperation"/> through
        /// <see cref="Relations.Database.RunOnTables{TOperation}"/>, which notifies every layer that re-staged rows, so
        /// the tables' pass's merge applies them. The cursors' Dirty positions are committed with the rest after the
        /// loop (<see cref="ContextTables.CommitCursors"/>). Cost: the container pass's, plus one rebuild-index probe
        /// per dirty id and own layer, plus the rows found; nothing more when no container concerns the pass.
        /// </para>
        /// <para>
        /// Only the union-find merges here (<see cref="Relations.Database.MergeTable"/>), as at the root, where nothing
        /// else is pending when the containers rebuild. In a child the first pass's catch-up has staged copies of
        /// ancestor rows (<see cref="ContextTables.CatchUpRows"/>), one kept as it is beside an own row with its key;
        /// merged after a container union, such a copy would be canonicalized under the winner while the own row,
        /// which only the tables' pass rewrites, still names the loser, and a function without a merge would refuse
        /// the two. The tables' pass merges the copies with the rewrites.
        /// </para>
        /// <para>
        /// The rebuild settles the first pass when its catch-up staged such copies. The ancestor content they name
        /// was made against the ancestor's union-find, where egglog would have made it against the child's, finding
        /// the containers the child holds: a vector of a vector of an id the child merged away rebuilds equal to one
        /// the child holds only after the container pass that merges the inner vector, and the pass that follows
        /// that merge reaches the outer one through its delta. One container pass per pass, as egglog runs at the
        /// root, would leave the outer vectors apart until the next pass, while the tables' pass met the copy with the
        /// own row that names the other. Every other pass runs one container pass, so where the child's own unions
        /// make its rows collapse before nested containers they name become equal, it refuses as egglog does; in a
        /// settled first pass the same collapse is accepted.
        /// </para>
        /// </remarks>
        /// <param name="epoch">The pass's epoch.</param>
        /// <param name="inherited">The ancestors' dirty ids, in the first pass; otherwise <c>null</c>.</param>
        /// <param name="settle">Whether to repeat the step until a container pass changes nothing.</param>
        /// <param name="pass">Set when the step changed something.</param>
        /// <returns>The non-local dirty ids the container passes found, whose ancestor rows the tables' pass
        /// re-stamps; valid until the next container pass.</returns>
        ReadOnlySpan<uint> RebuildContainerLayers(uint epoch, uint[]? inherited, bool settle, ref bool pass)
        {
            List<uint>? restamp = null;
            while (true)
            {
                if (contradicted)
                    return default;

                var summary = containerLayers!.RebuildPass(epoch, inherited);
                inherited = null;
                if (summary.Changed)
                {
                    database.MergeTable(store.UnionFindTable, epoch);
                    pass = true;
                }

                if (summary.DirtyCount > 0)
                {
                    var own = tables!.OwnRebuildTables();
                    if (own.Length > 0)
                        pass |= database.RunOnTables(own, new RefreshTableOperation(summary.Dirty.ToArray(), epoch));

                    var log = containerLayers.Log;
                    if (log.Active)
                        log.AppendDirty(summary.Found);
                }

                // The unions just merged are the next container pass's delta; one that changes nothing ends the step.
                if (settle == false || summary.Changed == false)
                {
                    if (restamp is null)
                        return summary.Restamp;

                    restamp.AddRange(summary.Restamp);
                    return CollectionsMarshal.AsSpan(restamp);
                }

                (restamp ??= new List<uint>()).AddRange(summary.Restamp);
            }
        }

    }

}
