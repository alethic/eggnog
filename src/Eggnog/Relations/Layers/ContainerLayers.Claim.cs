using System.Collections.Generic;
using System.Diagnostics;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerLayers
    {

        /// <summary>
        /// Makes <paramref name="id"/> an id the context's layer <paramref name="layer"/> may hold an entry under (the
        /// ownership rule, <see cref="Owns"/>): nothing when the id is local to the context or the layer shadows it
        /// already; otherwise the layer claims it (<see cref="Claim{TContainer}"/>). Every insert or rename into a
        /// layer's own environment goes through here first. Domain thread only.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type.</param>
        /// <param name="layer">The context's layer of the type.</param>
        /// <param name="id">The id.</param>
        /// <param name="state">The state a placement's merge runs in.</param>
        /// <returns><c>true</c> if the layer claimed the id.</returns>
        internal bool EnsureOwnable<TContainer>(ContainerEnv<TContainer> root, ContainerLayer<TContainer> layer, uint id, ExecutionState state)
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (Owns(layer, id))
                return false;

            Claim(root, layer, id, state);
            return true;
        }

        /// <summary>
        /// Claims the ancestor id <paramref name="id"/> for the context's layer <paramref name="layer"/>: shadows it,
        /// so that no ancestor's entry under it is visible from the context any more, and places a copy of each of
        /// those entries, read from the nearest level below that owns the id
        /// (<see cref="ContainerEnv{TContainer}.CollectById"/>, under that level's locks unless it is frozen, and the
        /// root's always), in the layer's own environment, where the context's container pass rebuilds it. Domain
        /// thread only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Placement merges
        /// (<see cref="ContainerEnv{TContainer}.InsertOwned(in TContainer, ulong, uint, ExecutionState)"/>): when the
        /// own environment holds the entry's content already, under an id it owns, the two ids merge through the type's
        /// merge function and the entry keeps the result, which the layer owns either way, so a claim never gives one
        /// content two entries. Usually there is one entry under the id; more where an ancestor's rebuild renamed one
        /// container onto another's id.
        /// </para>
        /// <para>
        /// The id joins the pass's claimed ids (<see cref="ContainerPassScratch.Claimed"/>), from which the pass
        /// rebuilds it. Cost: one probe per level for the owner, one probe sequence there, and one insert per entry;
        /// one shadow-set add.
        /// </para>
        /// </remarks>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type.</param>
        /// <param name="layer">The context's layer of the type.</param>
        /// <param name="id">The id: an ancestor's, neither local to the context nor shadowed by the layer.</param>
        /// <param name="state">The state a placement's merge runs in.</param>
        internal void Claim<TContainer>(ContainerEnv<TContainer> root, ContainerLayer<TContainer> layer, uint id, ExecutionState state)
            where TContainer : struct, IContainerValue<TContainer>
        {
            Debug.Assert(IsLocal(id) == false, "a child claims only ancestor ids");
            Debug.Assert(frozen == false, "a frozen context claims nothing");

            // Read before the id is shadowed, so the owner found is the level whose entries the context saw.
            var entries = new List<ContainerEntry<TContainer>>(1);
            CollectBelow(root, id, entries);
            var added = layer.Shadowed.Add(id);
            Debug.Assert(added, "an id the layer shadows already was claimed");

            var own = layer.Own;
            foreach (var entry in entries)
                own.InsertOwned(entry.Container, entry.Hash, id, state);

            var work = scratch!;
            work.Claimed.Add(id);
            work.Claims++;
        }

        /// <summary>
        /// Puts the rebuilt container <paramref name="container"/> back under <paramref name="id"/> over the context's
        /// view, for the layer <paramref name="layer"/>'s own environment: the insert of
        /// <see cref="LayeredInsertPolicy{TContainer}"/>, egglog's <c>insert_owned</c>
        /// (core-relations/src/containers/mod.rs:411-439) with the view standing for egglog's one hash-cons. Domain
        /// thread only.
        /// </summary>
        /// <remarks>
        /// <list type="number">
        /// <item><description>The own environment holds the content under <c>M</c>: the ids merge, and the entry keeps
        /// the result <c>r</c>, made ownable first (<see cref="EnsureOwnable{TContainer}"/>), so it is renamed only to
        /// an id the layer owns.</description></item>
        /// <item><description>A level below holds the content under an id <c>Y</c> the context sees
        /// (<see cref="TryGetIdBelow{TContainer}"/>): the ids merge. When <c>Y</c> wins, nothing is inserted: the
        /// content is visible under <c>Y</c> already, and <paramref name="id"/> loses. When <paramref name="id"/> wins,
        /// the layer claims <c>Y</c>, which places the content under <c>Y</c> in its own environment, and renames it to
        /// <paramref name="id"/>, made ownable first. So in egglog's p2 a child's rebuilt <c>[A]</c> under the root's
        /// id 2 meets the root's <c>[A]</c> under 3, keeps 2, and the root's entry is shadowed.</description></item>
        /// <item><description>Neither: <paramref name="id"/> is made ownable, and the content is inserted under
        /// it.</description></item>
        /// </list>
        /// <para>
        /// Each merge stages its union through <paramref name="state"/>, for the pass's merge to apply. Cost: one probe
        /// of the own environment and one per level below with a non-empty layer of the type, plus the root's, and the
        /// claims.
        /// </para>
        /// </remarks>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type.</param>
        /// <param name="layer">The context's layer of the type.</param>
        /// <param name="container">The rebuilt container, out of every structure of the own environment.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="id">Its old id, rebuilt: canonical in the context.</param>
        /// <param name="state">The state the merges run in.</param>
        /// <returns>The id the container ends up with in the context's view.</returns>
        internal uint Insert<TContainer>(ContainerEnv<TContainer> root, ContainerLayer<TContainer> layer, in TContainer container, ulong hash, uint id, ExecutionState state)
            where TContainer : struct, IContainerValue<TContainer>
        {
            var own = layer.Own;
            var merge = own.MergeFunction;
            if (own.TryGetId(container, hash, out var current))
            {
                var result = merge.Merge(state, current, id);
                if (result == current)
                    return current;

                EnsureOwnable(root, layer, result, state);
                own.RenameContent(container, hash, result);
                return result;
            }

            if (TryGetIdBelow(root, container, hash, out var visible))
            {
                var result = merge.Merge(state, visible, id);
                if (result == visible)
                    return visible;

                Claim(root, layer, visible, state);
                EnsureOwnable(root, layer, result, state);
                var renamed = own.RenameContent(container, hash, result);
                Debug.Assert(renamed, "a claim did not place the content it was claimed for");
                return result;
            }

            EnsureOwnable(root, layer, id, state);
            return own.InsertOwned(container, hash, id, state);
        }

        /// <summary>
        /// Appends to <paramref name="destination"/> every entry stored under <paramref name="id"/> at the nearest
        /// level below the context that owns the id, or at the root when none does: what the context sees under the id
        /// before it claims it. A level that owns the id but holds nothing under it answers with nothing (the id was
        /// merged away there). Under that level's shard locks unless it is frozen; the root's always.
        /// </summary>
        void CollectBelow<TContainer>(ContainerEnv<TContainer> root, uint id, List<ContainerEntry<TContainer>> destination)
            where TContainer : struct, IContainerValue<TContainer>
        {
            for (var level = parent; level is not null; level = level.parent)
            {
                if (level.LayerOf(root) is { } below && level.Owns(below, id))
                {
                    below.Own.CollectById(id, destination, locked: level.frozen == false);
                    return;
                }
            }

            root.CollectById(id, destination, locked: true);
        }

        /// <summary>
        /// Gets the id of <paramref name="container"/> at the nearest level below the context, the root included, that
        /// holds it under an id no level above it, up to and including the context, shadows: the lookup by content of
        /// <see cref="TryGetId{TContainer}"/> without the context's own environment.
        /// </summary>
        internal bool TryGetIdBelow<TContainer>(ContainerEnv<TContainer> root, in TContainer container, ulong hash, out uint id)
            where TContainer : struct, IContainerValue<TContainer>
        {
            for (var level = parent; level is not null; level = level.parent)
            {
                if (level.LayerOf(root) is not { IsEmpty: false } below)
                    continue;

                if (below.TryGetId(container, hash, level.frozen, out id) && ShadowedAbove(level, root.TypeId, id) == false)
                    return true;
            }

            if (root.TryGetId(container, hash, out id) && ShadowedAbove(null, root.TypeId, id) == false)
                return true;

            id = 0;
            return false;
        }

    }

}
