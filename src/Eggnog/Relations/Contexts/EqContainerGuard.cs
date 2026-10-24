using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The eq-container guard of a child's rebuild: a child may not displace an id that an eq-sort container (one
    /// holding e-class ids) holds, because the container would have to be rebuilt in the child, which children never do
    /// (they never call <see cref="Database.RebuildContainers"/>). A child may look eq-sort containers up but never
    /// interns one (<see cref="ContainerEnv{TContainer}"/> refuses it), and may not displace an id one holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the child's own displacements are checked, against the containers the root holds: an imported displacement
    /// was already renamed by the root's own container rebuild, and an intermediate ancestor's own loser is its own
    /// guard's business (the child is clean only while its parent is).
    /// </para>
    /// <para>
    /// The guard runs after each pass of <see cref="Context.Rebuild"/>, and before the loop when an ancestor's
    /// container log gained containers since the guard last read it (<see cref="IsBehind"/>). It checks two deltas,
    /// each once: the journal rows it has not checked, against every container the root holds; and, while the root is
    /// not settled, the Added entries of the root's container log past the child's cursor into it
    /// (<see cref="ContainerLog"/>), the containers that appeared there since, against every own displacement. The
    /// Added entries of the ancestors in between are consumed unread: the engine places nothing in their layers. The
    /// root (while it is not frozen) may intern, or its container rebuild may produce, a container naming an id the
    /// child displaced earlier, which the journal's delta would miss; the child is not clean until it checked against
    /// it (<see cref="ContextTables.NeedsCatchUp"/>). A child with no displacement moves its cursors past such entries
    /// unread: it checks a later displacement against every container. When the guard throws, the context is faulted
    /// and must be disposed.
    /// </para>
    /// <para>
    /// Cost: nothing unless a container type holding ids is registered; then one exact element-index probe per type and
    /// new own displacement, plus, per container that appeared at the root since the last check, one probe and one
    /// journal lookup per element.
    /// </para>
    /// </remarks>
    internal static class EqContainerGuard
    {

        /// <summary>
        /// Gets whether the guard of <paramref name="context"/> is behind its ancestors' containers: an unsettled
        /// ancestor's container log holds Added entries past the context's cursor
        /// (<see cref="ContextTables.HasNewAddedEntries"/>), which the rebuild then checks before its loop, whatever
        /// else is to be done. It does not look at the journal: the rows a write merged since the last rebuild do not
        /// make the guard behind, and are left to the guard after the first pass, but a run before the loop checks
        /// them as well (<see cref="Check"/>). O(unsettled ancestors).
        /// </summary>
        /// <param name="context">A child context.</param>
        /// <returns><c>true</c> if an ancestor's container log gained entries since the guard last read it.</returns>
        public static bool IsBehind(Context context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return context.Tables is { } tables && tables.HasNewAddedEntries;
        }

        /// <summary>
        /// Checks the journal rows of <paramref name="context"/> from <paramref name="checkedRows"/> on against every
        /// container the root holds, and, while the root is not settled, the containers the root's container log gained
        /// since the context's cursor into it against every own displacement; then moves <paramref name="checkedRows"/>
        /// to the journal's end, and every Added cursor to its log's end, past the entries of the ancestors in between
        /// unread (<see cref="ContextTables.ConsumeAdded"/>).
        /// </summary>
        /// <param name="context">A child context, in its rebuild.</param>
        /// <param name="checkedRows">The journal rows checked so far.</param>
        /// <exception cref="NotSupportedException">An own displacement is held by an eq-sort container.</exception>
        public static void Check(Context context, ref int checkedRows)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.Journal is not { } journal)
                return;

            var values = context.Database.Containers;
            var end = journal.Count;
            if (checkedRows < end && values.HasEqContainers)
            {
                var ids = journal.Ids;
                for (var row = checkedRows; row < end; row++)
                {
                    var id = ids[row];
                    if (context.IsOwnDisplacement(id) && values.AnyEqContainerMentions(id))
                        ThrowDisplacedElement(id);
                }
            }

            checkedRows = end;

            // The containers that appeared at the root since the child last looked. Without a displacement there is
            // nothing to check them against, and the cursors move past them unread.
            var tables = context.Tables!;
            if (tables.HasNewAddedEntries == false)
                return;

            var added = end > 0 && values.HasEqContainers ? new List<ContainerLogEntry>() : null;
            tables.ConsumeAdded(added);
            if (added is not { Count: > 0 })
                return;

            // Sorted, since the root's parallel tasks buffer in no fixed order: the first refusal does not depend on
            // the schedule.
            added.Sort();
            var elements = new List<uint>();
            foreach (var entry in added)
            {
                elements.Clear();
                if (values.GetEnvironment(entry.Type).TryCollectValues(entry.Id, entry.Hash, elements) == false)
                    continue;

                foreach (var element in elements)
                    if (journal.Contains(element) && context.IsOwnDisplacement(element))
                        ThrowDisplacedElement(element);
            }
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowDisplacedElement(uint id)
        {
            throw new NotSupportedException($"Id {id} is held by a container of e-class ids, and a child context displaced it; a child may not displace an id an eq-sort container holds. The context is faulted; dispose it.");
        }

    }

}
