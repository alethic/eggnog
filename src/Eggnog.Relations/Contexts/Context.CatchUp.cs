using System;
using System.Diagnostics;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Catch-up step 1 (§5.6, C8): re-keys the child's union-find over the displacements its parent made since the
        /// child last caught up, through one cursor into the parent's displaced ids (X7), journaling at
        /// <paramref name="epoch"/> every id that stops being canonical in the child.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For each new parent displacement <c>d</c>, in the parent's row order, the child computes <c>d</c>'s final
        /// canonical in the parent and re-keys its overlay (<see cref="UnionFindOverlay.Rekey"/>); the ids the re-key
        /// reports lost are journaled unless they already are. The parent's displaced ids re-export every displacement
        /// visible at the parent, its own imports included, so one cursor suffices. Afterwards every overlay member is
        /// canonical in the parent again (I6) and the journal is complete (I8).
        /// </para>
        /// <para>
        /// Cost: O(1) expected per parent displacement plus the sizes of the classes involved; nothing when the child is
        /// current, which is always the case under a frozen parent after the first catch-up. Called by
        /// <see cref="Rebuild"/>'s first pass, inside the domain, after the parent was rebuilt or with a frozen parent.
        /// </para>
        /// <para>
        /// Unions staged in the child and not merged yet would be merged after the imports, possibly with an older
        /// timestamp than they carry, so they are refused when there is something to catch up with. (The spec's sketch
        /// refuses them before checking the cursor; refusing only when a re-key happens is equivalent for every caller,
        /// whose staged unions <see cref="Rebuild"/> merges right after, and keeps a rebuild with nothing to catch up
        /// with from failing.)
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
                Debug.Assert(idBlocks!.Contains(d) == false, "a parent displaced an id local to its child (I10)");

                // The final canonical of d in the parent: within a batch, chains d → p1 → p2 resolve to p2 (edge case 5).
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

    }

}
