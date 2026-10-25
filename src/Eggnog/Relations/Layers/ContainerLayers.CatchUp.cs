using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerLayers
    {

        /// <summary>
        /// The pre-loop scan of the context's rebuild (<see cref="Context.Rebuild"/>), before its clean test: reads the
        /// Added entries the unsettled ancestors' container logs gained since the context's cursors into them, keeps
        /// for the rebuild's catch-up (<see cref="CatchUp"/>) those that concern the context, consumes the others, and
        /// moves the cursors to the logs' ends. Domain thread only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every log with something new is drained and read. When the context has neither a displacement nor a
        /// container layer, or no registered type holds ids, nothing an entry holds can concern it until it displaces
        /// something, whose container pass claims every container that holds the loser through the element indexes of
        /// the levels below, so the cursors move past the entries unread. Otherwise the entries are sorted by ancestor,
        /// type, id and content hash, since the ancestors' parallel tasks buffer theirs in no fixed order, and each is
        /// classified against the context (<see cref="ContainerCatchUpClass"/>): those it must adopt, claim or settle
        /// are kept, and the others, hidden above, gone or inert, are consumed. An entry inert now stays so through the
        /// catch-up, or reaches the context through the ordinary delta: an id that the catch-up's re-key of the
        /// union-find makes lose is journaled, and the pass claims what holds it, or is it.
        /// </para>
        /// <para>
        /// The scan stages nothing and changes no view, so, like a clean test, it may run inside a callback; when it
        /// keeps nothing and nothing else is to be done, the rebuild returns without taking an epoch. Cost: nothing
        /// unless a log moved; then one drain per log, which moves the entries the ancestors buffered into their logs,
        /// and nothing more allocated when the cursors move unread; otherwise the sort, and per entry a shadow test per
        /// level above its ancestor, one probe sequence there for the contents the entry names, usually one, and per
        /// content a rebuild against the context's union-find and one probe of the context's layer of its type.
        /// </para>
        /// </remarks>
        public void ScanAdds()
        {
            var tables = owner.Tables!;
            if (tables.HasNewAddedEntries == false)
                return;

            var values = owner.Database.Containers;
            if (values.HasEqContainers == false || (owner.Journal!.Count == 0 && IsEmpty))
            {
                tables.SkipAdded();
                return;
            }

            var added = new List<(int Depth, ContainerLogEntry Entry)>();
            var ends = tables.ReadAdded(added);
            if (added.Count > 0)
            {
                added.Sort(CompareEntries);
                var rebuilder = owner.UnionFindTable.GetRebuilder(ReadOnlySpan<ColumnId>.Empty)!;
                foreach (var item in added)
                {
                    switch (values.GetEnvironment(item.Entry.Type).ClassifyAdded(this, item.Depth, item.Entry, rebuilder))
                    {
                        case ContainerCatchUpClass.Adopt:
                        case ContainerCatchUpClass.Candidate:
                        case ContainerCatchUpClass.Collision:
                            (pending ??= new List<(int Depth, ContainerLogEntry Entry)>()).Add(item);
                            break;
                    }
                }
            }

            // Committed once the entries are read and classified, so a scan that throws reads them again.
            tables.CommitAdded(ends);
        }

        /// <summary>
        /// The container step of the first pass of the context's rebuild, after its union-find and rows caught up
        /// (<see cref="Context.Rebuild"/>): classifies again the entries the pre-loop scan kept, now against the
        /// caught-up union-find; settles each collision at once; and queues, for the pass that follows
        /// (<see cref="RebuildPass"/>), the ids to claim and the contents to adopt. Domain thread only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What each class gets (<see cref="ContainerCatchUpClass"/>): a candidate's id is claimed in the pass's phase
        /// A, so its phase B rebuilds the copy; a collision, an ancestor entry <c>(K, X)</c> the context sees while its
        /// own layer holds <c>K</c> under <c>L</c>, merges the two ids at once, and when <c>X</c> wins the layer's
        /// entry is taken out, since the context sees <c>K</c> under <c>X</c> already, and when <c>L</c> wins the
        /// layer claims <c>X</c>, whose placement merges into <c>L</c> (<see cref="SettleCollision{TContainer}"/>); an
        /// entry to adopt, content an ancestor put on an id the context shadows, is rebuilt against the context's
        /// union-find in the pass's phase B and inserted under the id's canonical id, which merges with an equal
        /// content the view holds (<see cref="Insert{TContainer}"/>).
        /// </para>
        /// <para>
        /// Which entries are adopted. An ancestor puts content on an id <c>X</c> by renaming a container onto it, or by
        /// reinserting a rebuilt one under it, after a union at that level in which another id <c>Y</c> lost to
        /// <c>X</c>, and by rebuilding a content it held under <c>X</c> in place. The last is the successor of a
        /// content the context copied when it claimed <c>X</c>, which its copy, following the context's own rebuild
        /// history, stands for. The first two are content the context's view would lose behind its shadow. When the
        /// context shadows <c>Y</c> too, it holds a copy of <c>Y</c>'s content of its own, which its pass moves onto
        /// <c>X</c> as the displacement reaches it, and the ancestor's version is a second history of that content. A
        /// level strictly between the ancestor and the context that shadows <c>Y</c> holds no copy of it under
        /// <c>X</c>: it would have claimed <c>X</c> to put one there, and the entry would be hidden above. It
        /// either merged <c>X</c> into the id of an equal content, so that no displacement is <c>X</c> in the parent,
        /// or dropped its copy, the same content being visible under <c>X</c> below it, which then only the adoption
        /// keeps in the context's view. So an entry is adopted only when one of the displacements the context imported
        /// in this catch-up, <paramref name="imported"/>, is <c>X</c> in the parent and the context's own layer of the
        /// entry's type does not shadow it (<see cref="KeepArrivals"/>): a content arrived under <c>X</c>. For
        /// containers whose rebuild does not depend on the order of their unions, adopting a successor or a second
        /// history would change nothing, since the insert finds the copy; for a map whose keys collapse it would add a
        /// second content under <c>X</c>. When an arrival and a successor come in one catch-up, both are adopted, and
        /// such a map may then hold both under <c>X</c>.
        /// </para>
        /// <para>
        /// The ancestors' new Dirty ids are not classified: an ancestor that rebuilds a container in place under an id
        /// it keeps records the new content in an Added entry too, which the pre-loop scan reads in the same catch-up;
        /// one whose rebuild makes one of two contents under the id equal to the other leaves only that other, an entry
        /// the context saw before; and an id dirty only through the closure holds what it held. The unions the settled
        /// collisions stage are flushed here, and the rebuild merges them before the pass. Cost: nothing when the scan
        /// kept nothing; otherwise per entry the scan's, plus, when an entry is to be adopted, one find in the parent
        /// per imported displacement and a shadow test for each one that is the entry's id in the parent.
        /// </para>
        /// </remarks>
        /// <param name="epoch">The pass's epoch.</param>
        /// <param name="imported">The parent's displaced ids the context's union-find caught up with in this pass.</param>
        /// <returns><c>true</c> if a collision was settled: its unions are staged, for the rebuild to merge.</returns>
        internal bool CatchUp(uint epoch, ReadOnlySpan<uint> imported)
        {
            var entries = pending;
            pending = null;
            var values = owner.Database.Containers;
            if (entries is not { Count: > 0 } || values.HasEqContainers == false)
                return false;

            entries.Sort(CompareEntries);
            var work = scratch ??= new ContainerPassScratch();
            work.Epoch = epoch;
            work.State = null;
            work.Rebuilder = null;
            work.ClearCatchUp();
            var rebuilder = PassRebuilder();
            var settled = false;
            for (var i = 0; i < entries.Count; i++)
            {
                var (depth, entry) = entries[i];
                if (i > 0 && entries[i - 1] == entries[i])
                    continue;

                var env = values.GetEnvironment(entry.Type);
                switch (env.ClassifyAdded(this, depth, entry, rebuilder))
                {
                    case ContainerCatchUpClass.Adopt:
                        work.Adoptions.Add((depth, entry));
                        break;
                    case ContainerCatchUpClass.Candidate:
                        work.CatchUpClaims.Add(entry);
                        break;
                    case ContainerCatchUpClass.Collision:
                        env.SettleCollision(this, depth, entry, PassState());
                        settled = true;
                        break;
                }
            }

            if (work.Adoptions.Count > 0)
                KeepArrivals(work.Adoptions, imported);

            if (work.State is { } state)
            {
                state.Flush();
                owner.Database.RecordFailure(state);
            }

            work.State = null;
            work.Rebuilder = null;
            return settled;
        }

        /// <summary>
        /// Gets the class of <paramref name="entry"/>, an Added entry of the container log of the ancestor at depth
        /// <paramref name="depth"/>, against the context (<see cref="ContainerCatchUpClass"/>, in its order), with
        /// <paramref name="rebuilder"/>, the context's union-find canonicalizer: over every content the entry names
        /// (<see cref="Resolve{TContainer}"/>), a candidate when one of them is not canonical in the context, and a
        /// collision when the context's layer holds one of them under another id. Changes nothing. Domain thread only.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the entry's type.</param>
        /// <param name="depth">The ancestor's depth.</param>
        /// <param name="entry">The entry.</param>
        /// <param name="rebuilder">The context's union-find canonicalizer.</param>
        /// <returns>The class.</returns>
        internal ContainerCatchUpClass ClassifyAdded<TContainer>(ContainerEnv<TContainer> root, int depth, ContainerLogEntry entry, Rebuilder rebuilder)
            where TContainer : struct, IContainerValue<TContainer>
        {
            var id = entry.Id;
            if (ShadowedBetween(depth, root.TypeId, id))
                return ContainerCatchUpClass.HiddenAbove;

            var contents = CollectionsMarshal.AsSpan(Resolve(root, depth, entry));
            if (contents.IsEmpty)
                return ContainerCatchUpClass.Gone;

            var layer = LayerOf(root);
            if (layer is not null && layer.Shadowed.Contains(id))
                return ContainerCatchUpClass.Adopt;

            if (rebuilder.RebuildValue(id) != id)
                return ContainerCatchUpClass.Candidate;

            foreach (ref readonly var content in contents)
                if (content.Container.TryRebuild(rebuilder, out _))
                    return ContainerCatchUpClass.Candidate;

            if (layer is { IsEmpty: false })
                foreach (ref readonly var content in contents)
                    if (layer.Own.TryGetId(content.Container, entry.Hash, out var held) && held != id)
                        return ContainerCatchUpClass.Collision;

            return ContainerCatchUpClass.Inert;
        }

        /// <summary>
        /// Settles a collision (<see cref="ContainerCatchUpClass.Collision"/>): the ancestor at depth
        /// <paramref name="depth"/> holds, under <c>X</c>, a content <c>K</c> the entry <paramref name="entry"/> names,
        /// which the context sees there, and the context's layer of its type holds <c>K</c> under <c>L</c>. The ids
        /// merge through the type's merge function, staging their union: when <c>X</c> wins, the layer's entry is
        /// taken out (<see cref="ContainerEnv{TContainer}.RemoveContent"/>), since the context sees <c>K</c> under
        /// <c>X</c> already; when <c>L</c> wins, the layer claims <c>X</c>, whose placement merges <c>K</c> into
        /// <c>L</c> (<see cref="Claim{TContainer}"/>). Either way the view holds <c>K</c> once, and the id that lost is
        /// journaled when the rebuild merges the union, so the pass that follows rebuilds what the layer holds under
        /// it, and the rows naming it, from its delta. Each content the entry names is settled in turn, until a claim
        /// of <c>X</c> has placed them all. Domain thread only.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the entry's type.</param>
        /// <param name="depth">The ancestor's depth.</param>
        /// <param name="entry">The entry.</param>
        /// <param name="state">The state the merge stages its union in.</param>
        internal void SettleCollision<TContainer>(ContainerEnv<TContainer> root, int depth, ContainerLogEntry entry, ExecutionState state)
            where TContainer : struct, IContainerValue<TContainer>
        {
            var layer = LayerOf(root)!;
            var own = layer.Own;
            foreach (ref readonly var content in CollectionsMarshal.AsSpan(Resolve(root, depth, entry)))
            {
                // A claim placed every content under the id, each merged with an equal one the layer held.
                if (layer.Shadowed.Contains(entry.Id))
                    return;

                if (own.TryGetId(content.Container, entry.Hash, out var held) == false || held == entry.Id)
                    continue;

                var result = own.MergeFunction.Merge(state, held, entry.Id);
                if (result == entry.Id)
                {
                    var removed = own.RemoveContent(content.Container, entry.Hash, out _);
                    Debug.Assert(removed, "a layer's entry vanished while a collision with it was settled");
                }
                else
                {
                    Claim(root, layer, entry.Id, state);
                }
            }
        }

        /// <summary>
        /// The adoptions of phase B (<see cref="RebuildType{TContainer}"/>), after the layer's rebuild: each content an
        /// entry the catch-up queued for the type names, read again at its ancestor, is rebuilt against the context's
        /// union-find and inserted under the entry's id's canonical id (<see cref="Insert{TContainer}"/>), unless the
        /// view holds it there already.
        /// </summary>
        void AdoptEntries<TContainer>(ContainerEnv<TContainer> root, ContainerLayer<TContainer> layer, Rebuilder rebuilder, ExecutionState state, ContainerRebuildSummary summary)
            where TContainer : struct, IContainerValue<TContainer>
        {
            foreach (var (depth, entry) in CollectionsMarshal.AsSpan(scratch!.Adoptions))
            {
                if (entry.Type != root.TypeId)
                    continue;

                var target = rebuilder.RebuildValue(entry.Id);
                foreach (ref readonly var content in CollectionsMarshal.AsSpan(Resolve(root, depth, entry)))
                {
                    var container = content.Container;
                    var hash = entry.Hash;
                    if (container.TryRebuild(rebuilder, out var rebuilt))
                    {
                        container = rebuilt;
                        hash = rebuilt.ComputeHash();
                    }

                    if (TryGetId(root, container, hash, out var seen) && seen == target)
                        continue;

                    Insert(root, layer, container, hash, target, state);
                    summary.NoteChange();
                }
            }
        }

        /// <summary>
        /// Gets whether the catch-up queued an entry of the type with id <paramref name="type"/> to adopt.
        /// </summary>
        bool HasAdoptions(ContainerTypeId type)
        {
            foreach (var (_, entry) in CollectionsMarshal.AsSpan(scratch!.Adoptions))
                if (entry.Type == type)
                    return true;

            return false;
        }

        /// <summary>
        /// Keeps, of the entries to adopt, those under an id <c>X</c> into which content arrived from an id whose
        /// content the context does not hold itself: one of the displacements the context imported,
        /// <paramref name="imported"/>, is <c>X</c> in the parent, and the context's own layer of the entry's type does
        /// not shadow that displaced id (see <see cref="CatchUp"/>). A displaced id the context shadows, its own losers
        /// among them, has the context's copy of its content, which follows the displacement onto <c>X</c> in the
        /// context's pass; a level in between that shadows it holds no copy of it under <c>X</c>, which that level
        /// does not shadow. One find in the parent per imported displacement, and a shadow test for each one that is an
        /// entry's id in the parent.
        /// </summary>
        void KeepArrivals(List<(int Depth, ContainerLogEntry Entry)> adoptions, ReadOnlySpan<uint> imported)
        {
            var targets = new HashSet<uint>();
            foreach (var (_, entry) in adoptions)
                targets.Add(entry.Id);

            // The imported displacements into the ids of the entries, sorted by that id.
            var arrivals = new List<(uint Target, uint Loser)>();
            foreach (var id in imported)
            {
                var canonical = owner.FindInParent(id);
                if (targets.Contains(canonical))
                    arrivals.Add((canonical, id));
            }

            arrivals.Sort();
            var kept = 0;
            for (var i = 0; i < adoptions.Count; i++)
                if (Arrived(CollectionsMarshal.AsSpan(arrivals), adoptions[i].Entry))
                    adoptions[kept++] = adoptions[i];

            adoptions.RemoveRange(kept, adoptions.Count - kept);
        }

        /// <summary>
        /// Gets whether one of <paramref name="arrivals"/>, the imported displacements sorted by the id they are in the
        /// parent, is <paramref name="entry"/>'s id there, and the context's own layer of the entry's type does not
        /// shadow it: the test of <see cref="KeepArrivals"/>.
        /// </summary>
        bool Arrived(ReadOnlySpan<(uint Target, uint Loser)> arrivals, ContainerLogEntry entry)
        {
            var low = 0;
            var high = arrivals.Length;
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                if (arrivals[middle].Target < entry.Id)
                    low = middle + 1;
                else
                    high = middle;
            }

            var own = LayerAt(entry.Type);
            for (var i = low; i < arrivals.Length && arrivals[i].Target == entry.Id; i++)
                if (own is null || own.Shadowed.Contains(arrivals[i].Loser) == false)
                    return true;

            return false;
        }

        /// <summary>
        /// The list <see cref="Resolve{TContainer}"/> fills, a list of entries of the container type of the Added entry
        /// it resolved last, which it reuses while the entries it resolves keep that type, as the catch-up's come
        /// sorted by ancestor and type; <c>null</c> before the first. Domain thread only.
        /// </summary>
        object? resolved;

        /// <summary>
        /// Gets every entry the ancestor at depth <paramref name="depth"/> stores under <paramref name="entry"/>'s id
        /// with its content hash, in its environment of the type <paramref name="root"/> holds: the root's, under its
        /// locks, or that ancestor's layer's own environment, under its locks unless it is frozen. These are the
        /// contents the Added entry names: none once they are gone, usually one, and more where contents that share
        /// the id after a rename share the hash too. The list is the one the next call refills, and nothing changes it
        /// meanwhile. One probe sequence.
        /// </summary>
        List<ContainerEntry<TContainer>> Resolve<TContainer>(ContainerEnv<TContainer> root, int depth, ContainerLogEntry entry)
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (resolved is not List<ContainerEntry<TContainer>> contents)
                resolved = contents = new List<ContainerEntry<TContainer>>(1);

            contents.Clear();
            if (depth == 0)
            {
                root.CollectById(entry.Id, contents, locked: true);
            }
            else
            {
                var level = this;
                for (var d = owner.Depth; d > depth && level is not null; d--)
                    level = level.parent;

                if (level?.LayerOf(root) is { } layer)
                    layer.Own.CollectById(entry.Id, contents, locked: level.frozen == false);
            }

            var kept = 0;
            for (var i = 0; i < contents.Count; i++)
                if (contents[i].Hash == entry.Hash)
                    contents[kept++] = contents[i];

            contents.RemoveRange(kept, contents.Count - kept);
            return contents;
        }

        /// <summary>
        /// Gets whether a level strictly between the ancestor at depth <paramref name="depth"/> and the context shadows
        /// <paramref name="id"/> in its layer of the type with id <paramref name="type"/>: the ancestor's entries of the
        /// type under the id are then hidden from the context, and what the ancestor's container log says of them is
        /// that level's to catch up with. Lock-free; one shadow test per level in between.
        /// </summary>
        bool ShadowedBetween(int depth, ContainerTypeId type, uint id)
        {
            var level = parent;
            for (var d = owner.Depth - 1; d > depth && level is not null; d--, level = level.parent)
                if (level.LayerAt(type) is { } layer && layer.Shadowed.Contains(id))
                    return true;

            return false;
        }

        /// <summary>
        /// Orders the catch-up's entries by ancestor depth, then type, id and content hash
        /// (<see cref="ContainerLogEntry.CompareTo"/>), so that what the catch-up does does not depend on the order in
        /// which the ancestors' parallel tasks buffered them.
        /// </summary>
        static int CompareEntries((int Depth, ContainerLogEntry Entry) x, (int Depth, ContainerLogEntry Entry) y) =>
            x.Depth != y.Depth ? x.Depth.CompareTo(y.Depth) : x.Entry.CompareTo(y.Entry);

    }

}
