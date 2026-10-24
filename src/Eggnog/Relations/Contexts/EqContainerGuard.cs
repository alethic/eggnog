using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The eq-container guard of a child's rebuild: refuses a child that a container of classes (an eq-sort container,
    /// one holding e-class ids) concerns when a writable ancestor gained it after the child last caught up, which the
    /// child's container pass does not reconcile: it rebuilds only the containers its own displacements reach as they
    /// are made (<see cref="ContainerLayers.RebuildPass"/>). A child may look eq-sort containers up but never interns
    /// one (<see cref="ContainerEnv{TContainer}"/> refuses it).
    /// </summary>
    /// <remarks>
    /// <para>
    /// What concerns the child. The guard reads, while an unsettled ancestor is not frozen and gains containers, the
    /// Added entries of its container log past the child's cursor into it (<see cref="ContainerLog"/>): the containers
    /// that appeared at that level since, interned, reinserted by a rebuild or renamed. An entry under an id that a
    /// level between its ancestor and the child shadows is that level's business, whose own guard reads it, and an
    /// entry that has gone since resolves to nothing. An entry under an id the child claimed itself (its layer of the
    /// entry's type shadows it) is content the ancestor put on that id, renamed or reinserted there, which the child's
    /// view hides: the guard keeps it, and after the rebuild's passes checks it (<see cref="CheckClaimedIds"/>). An
    /// entry that is the only one under its id at its ancestor is the container the child claimed, rebuilt in place
    /// there or claimed there too, which the child's copy stands for, following the child's own rebuild history; the
    /// child is refused only if its view holds nothing under the id's canonical id. An entry that shares its id, as one
    /// renamed onto it does, refuses the child unless its view holds the entry's content, rebuilt against its
    /// union-find, under the id's canonical id; a child does not yet adopt other content. Any other entry refuses the
    /// child when its id or one of its elements is a displacement the child's container pass has already handled (a
    /// journal row before <see cref="ContainerLayers.ContainerMark"/> that the child made itself): no later pass
    /// revisits the new container; or when the child's own layer of the type holds an equal container under another
    /// id: the view would hold the content twice. A displacement the pass has not handled yet reaches the new container
    /// in that pass, which claims every container that holds it, and a container the pass rebuilds equal to the new one
    /// merges with it.
    /// </para>
    /// <para>
    /// The guard runs before the loop of <see cref="Context.Rebuild"/> when an ancestor's container log gained
    /// containers since the guard last read it (<see cref="IsBehind"/>), even when nothing else is to be done; a child
    /// with neither a displacement nor a container layer moves its cursors past them unread, since nothing they hold
    /// can concern it until it displaces something, whose pass claims what holds it. Until it ran, the child is not
    /// clean (<see cref="ContextTables.NeedsCatchUp"/>), nor while it keeps entries under ids the child claimed
    /// (<see cref="ContainerLayers.HasEntriesOnClaimedIds"/>). When the guard throws, the context is faulted and must be
    /// disposed.
    /// </para>
    /// <para>
    /// Cost: nothing unless a container type holding ids is registered and an ancestor's log moved; then, per new
    /// entry, a shadow test per level above its ancestor, one probe and one journal lookup per element, and one probe
    /// of the child's layer of its type; per entry kept, one probe of its ancestor and one lookup through the view, by
    /// id, or, for an entry that shares its id, by its content rebuilt.
    /// </para>
    /// </remarks>
    internal static class EqContainerGuard
    {

        /// <summary>
        /// Gets whether the guard of <paramref name="context"/> is behind its ancestors' containers: an unsettled
        /// ancestor's container log holds Added entries past the context's cursor
        /// (<see cref="ContextTables.HasNewAddedEntries"/>), which the rebuild then checks before its loop, whatever
        /// else is to be done. O(unsettled ancestors).
        /// </summary>
        /// <param name="context">A child context.</param>
        /// <returns><c>true</c> if an ancestor's container log gained entries since the guard last read it.</returns>
        public static bool IsBehind(Context context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return context.Tables is { } tables && tables.HasNewAddedEntries;
        }

        /// <summary>
        /// Checks the containers the unsettled ancestors' container logs gained since the context's cursors into them
        /// against the context (the type's remarks), keeping those under an id the context claimed for
        /// <see cref="CheckClaimedIds"/>, then moves every Added cursor to its log's end
        /// (<see cref="ContextTables.ConsumeAdded"/>).
        /// </summary>
        /// <param name="context">A child context, in its rebuild.</param>
        /// <exception cref="NotSupportedException">A new container concerns the context.</exception>
        public static void Check(Context context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.Journal is not { } journal)
                return;

            var tables = context.Tables!;
            if (tables.HasNewAddedEntries == false)
                return;

            var layers = context.ContainerLayers!;
            var values = context.Database.Containers;
            var concerned = (journal.Count > 0 || layers.IsEmpty == false) && values.HasEqContainers;
            var added = concerned ? new List<(int Depth, ContainerLogEntry Entry)>() : null;
            tables.ConsumeAdded(added);
            if (added is not { Count: > 0 })
                return;

            // Sorted, since the ancestors' parallel tasks buffer in no fixed order: the first refusal does not depend
            // on the schedule.
            added.Sort(static (x, y) => x.Depth != y.Depth ? x.Depth.CompareTo(y.Depth) : x.Entry.CompareTo(y.Entry));
            var mark = layers.ContainerMark;
            var elements = new List<uint>();
            foreach (var (depth, entry) in added)
            {
                if (layers.ShadowedBetweenDepth(depth, entry.Id))
                    continue;

                // Content an ancestor put on an id the context claimed, which its view hides: checked once the passes
                // have rebuilt the context's copy (CheckClaimedIds).
                if (layers.LayerAt(entry.Type) is { } claimedIn && claimedIn.Shadowed.Contains(entry.Id))
                {
                    layers.KeepEntryOnClaimedId(depth, entry);
                    continue;
                }

                var env = EnvironmentAt(context, values, depth, entry.Type);
                elements.Clear();
                if (env is null || env.TryCollectValues(entry.Id, entry.Hash, elements) == false)
                    continue;

                if (IsHandledDisplacement(context, journal, mark, entry.Id))
                    ThrowDisplaced(entry.Id);

                foreach (var element in elements)
                    if (IsHandledDisplacement(context, journal, mark, element))
                        ThrowDisplaced(element);

                if (layers.LayerAt(entry.Type) is { IsEmpty: false } layer && env.TryFindEqual(entry.Id, entry.Hash, layer.OwnBase, out var own))
                    ThrowCollision(entry.Id, own);
            }
        }

        /// <summary>
        /// Checks, after the passes of the context's rebuild, the entries the guard kept because they lie under an id
        /// the context claimed (the type's remarks): refuses the context unless its view holds what each one stands for
        /// there, a container under the entry's id rebuilt for the only entry under its id, and otherwise the entry's
        /// content rebuilt against the context's union-find under that id
        /// (<see cref="ContainerEnvBase.ViewHoldsRebuilt"/>), then forgets them. Nothing when the guard kept none.
        /// </summary>
        /// <param name="context">A child context, at the end of its rebuild's passes.</param>
        /// <exception cref="NotSupportedException">The context's view lacks one, which the child does not yet
        /// adopt.</exception>
        public static void CheckClaimedIds(Context context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.ContainerLayers is not { HasEntriesOnClaimedIds: true } layers)
                return;

            var entries = layers.EntriesOnClaimedIds!;
            var values = context.Database.Containers;
            var rebuilder = context.UnionFindTable.GetRebuilder(ReadOnlySpan<ColumnId>.Empty)!;
            try
            {
                foreach (var (depth, entry) in entries)
                    if (EnvironmentAt(context, values, depth, entry.Type) is { } env && env.ViewHoldsRebuilt(entry.Id, entry.Hash, values.GetEnvironment(entry.Type), layers, rebuilder) == false)
                        ThrowNotAdopted(entry.Id);
            }
            finally
            {
                entries.Clear();
            }
        }

        /// <summary>
        /// Gets the environment of type <paramref name="type"/> at the ancestor of <paramref name="context"/> at depth
        /// <paramref name="depth"/>: the root's, or that ancestor's own environment of its layer of the type, if it has
        /// one.
        /// </summary>
        static ContainerEnvBase? EnvironmentAt(Context context, ContainerValues values, int depth, ContainerTypeId type)
        {
            if (depth == 0)
                return values.GetEnvironment(type);

            return context.Ancestors[depth].ContainerLayers!.LayerAt(type)?.OwnBase;
        }

        /// <summary>
        /// Gets whether <paramref name="id"/> is a displacement the context made itself and its container pass has
        /// handled: journaled before <paramref name="mark"/>, and canonical in the parent.
        /// </summary>
        static bool IsHandledDisplacement(Context context, DisplacedJournal journal, int mark, uint id) =>
            journal.TryGetRow(id, out var row) && row < mark && context.IsOwnDisplacement(id);

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowDisplaced(uint id)
        {
            throw new NotSupportedException($"Id {id} is held by a container of e-class ids that an ancestor context gained after this child context displaced it; a child does not yet rebuild a container its writable ancestor makes later. The context is faulted; dispose it.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNotAdopted(uint id)
        {
            throw new NotSupportedException($"An ancestor context put a container of e-class ids under id {id}, which this child context claimed, and the child's copy does not hold it; a child does not yet adopt a container its writable ancestor puts on an id it claimed. The context is faulted; dispose it.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowCollision(uint id, uint own)
        {
            throw new NotSupportedException($"A container of e-class ids that an ancestor context gained under id {id} equals the one this child context holds under id {own}; a child does not yet merge a container its writable ancestor makes later with its own. The context is faulted; dispose it.");
        }

    }

}
