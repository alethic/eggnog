using System;
using System.Diagnostics;
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
        /// The container part of a child's catch-up: re-stamps, at <paramref name="epoch"/>, the rows of the child's
        /// own layers that name a container the root rebuilt in place since the child last looked, from the Dirty
        /// streams of the unsettled ancestors' container logs (<see cref="ContextTables.ReadDirty"/>), so the child's
        /// semi-naive evaluation sees them as new, as egglog's <c>refresh_rows_for_values</c> does at the root. Root rows
        /// are re-stamped by the root itself and arrive as appends; ancestors in between refresh their own rows in their
        /// own rebuilds.
        /// </summary>
        /// <remarks>
        /// Runs <see cref="RefreshTableOperation"/> through <see cref="Relations.Database.RunOnTables{TOperation}"/>, which
        /// notifies every layer that re-staged rows, so the pass's merge applies them. The cursors' Dirty positions are
        /// committed with the rest after the loop (<see cref="ContextTables.CommitCursors"/>). Cost: nothing when no log
        /// has grown; otherwise one rebuild-index probe per dirty id and own layer, plus the rows found.
        /// </remarks>
        /// <param name="epoch">The pass's epoch, which the re-staged rows get.</param>
        /// <returns>Whether any row was re-staged.</returns>
        bool RefreshDirtyContainerIds(uint epoch)
        {
            var dirty = tables!.ReadDirty();
            if (dirty.Length == 0)
                return false;

            var own = tables!.OwnRebuildTables();
            return own.Length > 0 && database.RunOnTables(own, new RefreshTableOperation(dirty, epoch));
        }

    }

}
