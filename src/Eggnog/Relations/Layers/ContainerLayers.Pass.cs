using System;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerLayers
    {

        /// <summary>
        /// The working storage of the container passes, made by the first pass that may have work and dropped when the
        /// context freezes or is disposed. Domain thread only.
        /// </summary>
        ContainerPassScratch? scratch;

        /// <summary>
        /// Gets whether the container passes' working storage exists: made by a pass that may have work, and dropped
        /// at freeze and dispose. A test hook. Domain thread only.
        /// </summary>
        internal bool HasPassStorage => scratch is not null;

        /// <summary>
        /// One container pass of the context at <paramref name="epoch"/>, the container step of each pass of its
        /// rebuild (<see cref="Context.Rebuild"/>), before its tables' pass: claims the ancestor containers that hold
        /// its own new losers, rebuilds what its layers hold against its union-find, merging containers that become
        /// equal in its view, and closes the dirty ids over the view. egglog's <c>rebuild_containers</c>
        /// (core-relations/src/free_join/mod.rs:357-363) in the copy egglog's push makes, run over the context's
        /// layers. Domain thread only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The delta is the journal's rows since the container mark (<see cref="ContainerMark"/>), which the pass moves
        /// to the journal's end at its start: the losers of the unions its merges stage join the journal when the
        /// rebuild merges them after the pass, and reach the next pass, as a nested container's do at the root.
        /// </para>
        /// <para>
        /// For each container type that holds ids (<see cref="RebuildType{TContainer}"/>): phase A claims, for each own
        /// loser of the delta that is not local, the loser itself when it is a container id the context sees at a level
        /// below, and every container a level below lists for it in its element index that the context sees
        /// (<see cref="Claim{TContainer}"/>). An imported displacement needs none: the ancestor that displaced it
        /// rebuilt its own view first. Phase B rebuilds the layer's own environment: incrementally, over the delta's
        /// ids, the containers its element index lists for them and the claimed ids, when egglog's test for an
        /// incremental rebuild holds (<see cref="ContainerEnv{TContainer}.IsIncremental"/>, more than 1000 containers),
        /// and otherwise in full, serially; each rebuilt container goes back through
        /// <see cref="LayeredInsertPolicy{TContainer}"/>, and every id an insert claims is rebuilt in the same pass.
        /// </para>
        /// <para>
        /// The dirty ids are closed upward over the view: the containers rebuilt in place, and
        /// <paramref name="inherited"/>, the ancestors' dirty ids that no level up to the context shadows, which the
        /// first pass reads from their container logs; the containers of the context's layers that hold a dirty id are
        /// dirty, and so, from a found id on, are the containers of the levels below that the context sees and that
        /// hold one, egglog's <c>expand_dirty_id_closure</c> (core-relations/src/containers/mod.rs:212-240) over the
        /// view. An inherited id's holders at the levels below were closed by the ancestors themselves.
        /// </para>
        /// <para>
        /// The merges stage their unions through one execution state at <paramref name="epoch"/>, made when the pass
        /// first claims an id or rebuilds a layer and flushed at the end: the rebuild merges them when the pass changed
        /// something. Nothing happens, and nothing is allocated, when no registered type holds ids or nothing is new to
        /// the pass (<see cref="ContainerPassSummary.Empty"/>); the mark still moves. The pass's storage is made by the
        /// first pass that may have work, one with a layer, inherited dirty ids or an own loser that a container below
        /// holds or is (<see cref="AnyLoserHeldBelow"/>), and reused until the context freezes or is disposed
        /// (<see cref="ContainerPassScratch"/>). Cost: O(own losers × depth × types) element-index probes, plus O(size)
        /// per revisited container, or O(the layer's size) for a full rebuild, plus the closure's index fan-out.
        /// </para>
        /// </remarks>
        /// <param name="epoch">The pass's epoch.</param>
        /// <param name="inherited">The ancestors' dirty ids to close over, or empty.</param>
        /// <returns>What the pass did; valid until the next pass.</returns>
        public ContainerPassSummary RebuildPass(uint epoch, ReadOnlySpan<uint> inherited)
        {
            var journal = owner.Journal!;
            var from = containerMark;
            var end = journal.Count;
            containerMark = end;

            var values = owner.Database.Containers;
            if (values.HasEqContainers == false || (from == end && inherited.IsEmpty))
                return ContainerPassSummary.Empty;

            // Without layers or inherited dirty ids, only an own loser that a container below holds, or is, gives the
            // pass work: until one does, the pass makes no storage.
            var delta = journal.Ids.Slice(from, end - from);
            if (scratch is null && inherited.IsEmpty && IsEmpty && AnyLoserHeldBelow(values, delta) == false)
                return ContainerPassSummary.Empty;

            var work = scratch ??= new ContainerPassScratch();
            var summary = work.Summary;
            summary.Clear();

            // The own losers. A loser local to the context is left out: no level below holds a container naming it.
            var ownLosers = work.OwnLosers;
            ownLosers.Clear();
            foreach (var id in delta)
                if (idBlocks.Contains(id) == false && owner.IsOwnDisplacement(id))
                    ownLosers.Add(id);

            var rebuild = work.Rebuild;
            rebuild.Clear();
            work.Claims = 0;
            if (ownLosers.Count > 0 || (delta.IsEmpty == false && IsEmpty == false))
            {
                work.Epoch = epoch;
                work.State = null;
                work.Rebuilder = null;
                var count = values.TypeCount;
                for (var i = 0; i < count; i++)
                {
                    var env = values.GetEnvironment(ContainerTypeId.FromIndex(i));
                    if (env.HoldsIds)
                        env.RebuildLayer(this, delta, CollectionsMarshal.AsSpan(ownLosers), rebuild);
                }

                if (work.State is { } state)
                {
                    state.Flush();
                    owner.Database.RecordFailure(state);
                }

                work.State = null;
                work.Rebuilder = null;
            }

            if (rebuild.Changed || work.Claims > 0)
                summary.NoteChange();

            Close(values, rebuild.DirtyIds, inherited, work);
            return summary;
        }

        /// <summary>
        /// The pass over one container type (<see cref="RebuildPass"/>): phase A's claims, then phase B's rebuild of
        /// the context's layer of the type, then the rebuild of every id phase B claimed, until it claims none. Reached
        /// through the root's environment of the type (<see cref="ContainerEnvBase.RebuildLayer"/>). The pass's
        /// execution state and canonicalizer are made here, when the type first needs them.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type.</param>
        /// <param name="delta">The journal's ids since the container mark.</param>
        /// <param name="losers">The own losers among them that are not local.</param>
        /// <param name="summary">Receives the changes and the ids rebuilt in place.</param>
        internal void RebuildType<TContainer>(ContainerEnv<TContainer> root, ReadOnlySpan<uint> delta, ReadOnlySpan<uint> losers, ContainerRebuildSummary summary)
            where TContainer : struct, IContainerValue<TContainer>
        {
            var work = scratch!;
            var layer = LayerOf(root);
            var claimed = work.Claimed;
            claimed.Clear();

            // Phase A: the containers below that hold an own loser, or are one, and that the context sees. A level
            // below, the root included, holds one under the loser when the nearest level that owns the loser does.
            if (losers.IsEmpty == false)
            {
                var candidates = work.Candidates;
                candidates.Clear();
                for (var i = 0; i < losers.Length; i++)
                {
                    var loser = losers[i];
                    if ((layer is null || Owns(layer, loser) == false) && (parent is null ? root.ContainsId(loser) : parent.ContainsId(root, loser)))
                        candidates.Add(loser);

                    AddVisibleHoldersBelow(root, losers.Slice(i, 1), work.Holders, candidates);
                }

                if (candidates.Count > 0)
                {
                    layer ??= GetOrCreateLayer(root);
                    var state = PassState();
                    foreach (var id in candidates.AsSpan())
                        EnsureOwnable(root, layer, id, state);
                }
            }

            if (layer is null || layer.IsEmpty || (delta.IsEmpty && claimed.Count == 0))
            {
                claimed.Clear();
                return;
            }

            // Phase B: the layer's own environment, incrementally from the delta and the claims, or in full.
            var own = layer.Own;
            var policy = new LayeredInsertPolicy<TContainer>(this, layer, root);
            var rebuilder = PassRebuilder();
            var passState = PassState();
            var revisit = work.Revisit;
            if (ContainerEnv<TContainer>.IsIncremental(delta.Length + claimed.Count, own.Count, false))
            {
                revisit.Clear();
                foreach (var id in delta)
                {
                    revisit.Add(id);
                    own.ValueIndex.CollectContaining(id, revisit);
                }

                foreach (var id in CollectionsMarshal.AsSpan(claimed))
                    revisit.Add(id);

                claimed.Clear();
                own.RebuildIds(revisit.AsSpan(), rebuilder, passState, policy, summary);
            }
            else
            {
                claimed.Clear();
                own.RebuildAllSerial(rebuilder, passState, policy, summary);
            }

            // What the inserts claimed, copied from below as it was there, is rebuilt in this pass too.
            while (claimed.Count > 0)
            {
                revisit.Clear();
                foreach (var id in CollectionsMarshal.AsSpan(claimed))
                    revisit.Add(id);

                claimed.Clear();
                own.RebuildIds(revisit.AsSpan(), rebuilder, passState, policy, summary);
            }

            revisit.Clear();
        }

        /// <summary>
        /// Gets the current pass's execution state, made at the pass's epoch on first use: a pass makes one only when
        /// it claims an id or rebuilds a layer. Inside <see cref="RebuildPass"/> only.
        /// </summary>
        ExecutionState PassState()
        {
            var work = scratch!;
            return work.State ??= owner.Database.CreateEngineState(work.Epoch);
        }

        /// <summary>
        /// Gets the context's union-find canonicalizer for the current pass, made on first use. Inside
        /// <see cref="RebuildPass"/> only.
        /// </summary>
        Rebuilder PassRebuilder()
        {
            var work = scratch!;
            return work.Rebuilder ??= owner.UnionFindTable.GetRebuilder(ReadOnlySpan<ColumnId>.Empty)!;
        }

        /// <summary>
        /// Gets whether a pass over <paramref name="delta"/> may have work in a context without layers: whether one of
        /// its own losers that is not local is, at a level below the context, the root included, the id of a container
        /// of a type that holds ids, or an element of one. The test <see cref="RebuildPass"/> makes before the pass's
        /// first storage, so a child whose own losers no container concerns makes none. It allocates nothing and does
        /// not ask whether the context sees what it finds, which the pass then does. Per own loser and type that holds
        /// ids, the view's lookup by id and one element-index probe per level below.
        /// </summary>
        /// <param name="values">The root's container environments.</param>
        /// <param name="delta">The journal's ids since the container mark.</param>
        /// <returns><c>true</c> if a container below holds, or is, one of the own losers.</returns>
        bool AnyLoserHeldBelow(ContainerValues values, ReadOnlySpan<uint> delta)
        {
            var count = values.TypeCount;
            foreach (var id in delta)
            {
                if (idBlocks.Contains(id) || owner.IsOwnDisplacement(id) == false)
                    continue;

                for (var i = 0; i < count; i++)
                {
                    var root = values.GetEnvironment(ContainerTypeId.FromIndex(i));
                    if (root.HoldsIds == false)
                        continue;

                    if (parent is null ? root.ContainsId(id) : parent.ContainsId(root, id))
                        return true;

                    for (var level = parent; ; level = level.parent)
                    {
                        if ((level is null ? root : level.LayerAt(root.TypeId)?.OwnBase) is { } env && env.AnyContainerMentions(id))
                            return true;

                        if (level is null)
                            break;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Closes the pass's dirty ids over the context's view into the pass's summary: <paramref name="dirty"/>, the
        /// ids the pass rebuilt in place, and <paramref name="inherited"/>, the ancestors' (see
        /// <see cref="RebuildPass"/>). A found id's holders are looked up in the context's layers and in every level
        /// below (those the context sees); an inherited id's only in the context's layers, since the ancestors closed
        /// theirs. Type-erased, over every type that holds ids.
        /// </summary>
        void Close(ContainerValues values, ReadOnlySpan<uint> dirty, ReadOnlySpan<uint> inherited, ContainerPassScratch work)
        {
            if (dirty.IsEmpty && inherited.IsEmpty)
                return;

            var summary = work.Summary;
            foreach (var id in inherited)
                summary.NoteInherited(id);

            var frontier = work.Frontier;
            var reached = work.Reached;
            frontier.Clear();
            foreach (var id in dirty)
                if (summary.NoteFound(id, idBlocks.Contains(id)))
                    frontier.Add(id);

            var types = values.TypeCount;
            var seeds = inherited;
            while (frontier.Count > 0 || seeds.IsEmpty == false)
            {
                reached.Clear();
                var found = CollectionsMarshal.AsSpan(frontier);
                for (var i = 0; i < types; i++)
                {
                    var type = ContainerTypeId.FromIndex(i);
                    var env = values.GetEnvironment(type);
                    if (env.HoldsIds == false)
                        continue;

                    if (LayerAt(type) is { IsEmpty: false } layer)
                    {
                        layer.OwnBase.ExtendContainersContaining(seeds, reached);
                        layer.OwnBase.ExtendContainersContaining(found, reached);
                    }

                    if (found.IsEmpty == false)
                        AddVisibleHoldersBelow(env, found, work.Holders, reached);
                }

                seeds = default;
                frontier.Clear();
                foreach (var id in reached.AsSpan())
                    if (summary.NoteFound(id, idBlocks.Contains(id)))
                        frontier.Add(id);
            }

            reached.Clear();
            frontier.Clear();
        }

        /// <summary>
        /// Adds to <paramref name="output"/> the ids of the containers of the type <paramref name="root"/> holds that
        /// hold one of <paramref name="values"/> at a level below the context, the root included, and that the context
        /// sees: listed by that level's element index, under an id no level above it, up to and including the
        /// context, shadows. Phase A's lookup of an own loser's holders, the dirty closure's of a found id's, and the
        /// invariant checker's. Type-erased; it writes only <paramref name="holders"/> and <paramref name="output"/>, so
        /// the checker may run it on a frozen context from any thread with storage of its own.
        /// </summary>
        /// <param name="root">The root's environment of the type.</param>
        /// <param name="values">The values.</param>
        /// <param name="holders">Scratch: one level's holders; cleared.</param>
        /// <param name="output">Receives the ids not already in it.</param>
        void AddVisibleHoldersBelow(ContainerEnvBase root, ReadOnlySpan<uint> values, OrderedValueSet holders, OrderedValueSet output)
        {
            var level = parent;
            while (true)
            {
                var env = level is null ? root : level.LayerAt(root.TypeId)?.OwnBase;
                if (env is not null)
                {
                    holders.Clear();
                    env.ExtendContainersContaining(values, holders);
                    foreach (var holder in holders.AsSpan())
                        if (ShadowedAbove(level, root.TypeId, holder) == false)
                            output.Add(holder);
                }

                if (level is null)
                    break;

                level = level.parent;
            }

            holders.Clear();
        }

    }

}
