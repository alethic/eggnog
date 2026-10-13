using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The eq-container guard of a child's rebuild (colored-contexts spec §7.4, G-b, decision 9): a child may not
    /// displace an id that an eq-sort container (one holding e-class ids) holds, because the container would have to be
    /// rebuilt in the child, which children never do (they never call <see cref="Database.RebuildContainers"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the child's own displacements are checked: an imported displacement was already renamed by the root's own
    /// container rebuild (X31), and an intermediate ancestor's own loser is its own guard's business (the child is clean
    /// only while its parent is).
    /// </para>
    /// <para>
    /// The guard runs after each pass of <see cref="Context.Rebuild"/> over the journal rows not checked yet, and again
    /// over the whole journal whenever the store's eq-container version moved since the child last checked
    /// (<see cref="ContainerValues.EqVersion"/>): the root (while it is not frozen) may intern, or its container rebuild
    /// may produce, a container naming an id the child displaced earlier, and the child is not clean until it checked
    /// against it (<see cref="Context.IsClean"/>). The spec checks only the journal rows of each pass, which misses such a
    /// later container. When the guard throws, the context is faulted (X12) and must be disposed.
    /// </para>
    /// <para>
    /// Cost: nothing unless a container type holding ids is registered; then one exact element-index probe per type and
    /// new own displacement, plus one per type and own displacement each time the version moved.
    /// </para>
    /// </remarks>
    internal static class EqContainerGuard
    {

        /// <summary>
        /// Checks the journal rows of <paramref name="context"/> from <paramref name="checkedRows"/> on, or from the
        /// first row when the eq-container version moved since <paramref name="checkedVersion"/>, and moves both to the
        /// journal's end and the version read before the check.
        /// </summary>
        /// <param name="context">A child context, in its rebuild.</param>
        /// <param name="checkedRows">The journal rows checked so far.</param>
        /// <param name="checkedVersion">The eq-container version they were checked against.</param>
        /// <exception cref="NotSupportedException">An own displacement is held by an eq-sort container.</exception>
        public static void Check(Context context, ref int checkedRows, ref long checkedVersion)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.Journal is not { } journal)
                return;

            // Read before the check: a container interned meanwhile moves the version past it, so it is checked next time.
            var values = context.Database.Containers;
            var version = values.EqVersion;
            var start = version == checkedVersion ? checkedRows : 0;
            var end = journal.Count;
            if (start < end && values.HasEqContainers)
            {
                var ids = journal.Ids;
                for (var row = start; row < end; row++)
                {
                    var id = ids[row];
                    if (context.IsOwnDisplacement(id) && values.AnyEqContainerMentions(id))
                        ThrowDisplacedElement(id);
                }
            }

            checkedRows = end;
            checkedVersion = version;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowDisplacedElement(uint id)
        {
            throw new NotSupportedException($"Id {id} is held by a container of e-class ids, and a child context displaced it; eq-sort containers in children are not supported yet (decision 9). The context is faulted; dispose it.");
        }

    }

}
