using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

        /// <summary>
        /// Interim (M9.3, §17): refuses a rebuild of a child that would need row work, which arrives in M9.4, with
        /// <see cref="NotSupportedException"/>, before anything changes, so the child is not faulted. Called by
        /// <see cref="Rebuild"/> and <see cref="Freeze"/> on a child that is not clean.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Row work is needed, and refused, when:
        /// </para>
        /// <list type="bullet">
        /// <item><description>an ancestor that is not settled appended or replaced rows of a shared table since the child
        /// last caught up (row catch-up, §7.2; <see cref="ContextTables.AncestorRowsChanged"/>);</description></item>
        /// <item><description>the child's union-find changes in this rebuild or changed since its last one (an import of a
        /// parent displacement, unions merged since the journal mark, or unions staged and not merged) while a live row of
        /// an own layer names an id that stops being canonical (step 3, §7.3; <see cref="ContextTables.OwnRowsMention"/>).
        /// Those ids are the journal's rows since the mark plus, when the overlay is empty, the parent's new displacements;
        /// when they are not known yet (a staged union, or a re-key of a class the child merged), any live own row of a
        /// table with id columns counts;</description></item>
        /// <item><description>or the change makes an id that is canonical in the parent a loser in the child while a row
        /// the child sees through an ancestor names it (step 4, §7.3; <see cref="ContextTables.AncestorRowsMention"/>).
        /// Such losers are the own displacements journaled since the mark that are not local to the child, plus, when they
        /// are not known yet, whatever a staged union or a re-key of a class the child merged may displace.</description></item>
        /// </list>
        /// <para>
        /// Otherwise the rebuild is exact without row work: an ancestor whose only change is its union-find (its change
        /// stamp moved, no layer version did), imported by a child that holds no rows the import could make stale, needs
        /// no more than the re-key. The spec's interim refuses whenever <see cref="ContextTables.NeedsCatchUp"/>; that
        /// would refuse every catch-up after a root union, which the union-find (M9.2) supports. Cost: O(tables × depth)
        /// plus one rebuild-index probe per own loser and ancestor layer.
        /// </para>
        /// </remarks>
        /// <exception cref="NotSupportedException">The rebuild would need row work.</exception>
        void ThrowIfRowWorkPending()
        {
            if (tables is not { } t)
                return;

            Debug.Assert(parent is not null && overlay is not null && journal is not null && overlayTable is not null);
            if (t.NeedsCatchUp && t.AncestorRowsChanged())
                ThrowRowWork();

            var imports = unionFindCursor != parent.DisplacedCount;
            var staged = overlayTable.HasPendingUnions;
            if (imports == false && staged == false && journalMark == journal.Count)
                return;

            // Which ids stop being canonical is known unless a union is staged or a re-key may move the canonical of a
            // class the child merged; with an empty overlay an import loses exactly the parent's displaced id (§5.6).
            var unknown = staged || (imports && overlay.IsEmpty == false);
            var ids = journal.Ids;
            var changed = new List<uint>();
            for (var i = journalMark; i < ids.Length; i++)
                changed.Add(ids[i]);

            if (imports && unknown == false)
            {
                var displaced = parent.DisplacedIds;
                for (var i = unionFindCursor; i < displaced.Length; i++)
                    changed.Add(displaced[i]);
            }

            if (t.OwnRowsMention(CollectionsMarshal.AsSpan(changed), unknown))
                ThrowRowWork();

            // The own losers journaled since the mark that ancestor rows may name (I10: never a local id). An import is
            // no own loser: the ancestor rows naming it changed in the ancestor, which the first test refuses.
            var losers = new List<uint>();
            for (var i = journalMark; i < ids.Length; i++)
                if (idBlocks!.Contains(ids[i]) == false && IsOwnDisplacement(ids[i]))
                    losers.Add(ids[i]);

            if ((losers.Count > 0 || unknown) && t.AncestorRowsMention(CollectionsMarshal.AsSpan(losers), unknown))
                ThrowRowWork();
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowRowWork()
        {
            throw new NotSupportedException("Row catch-up arrives in M9.4.");
        }

    }

}
