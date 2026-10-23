using System.Diagnostics;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerLayers
    {

        /// <summary>
        /// Gets the container with id <paramref name="id"/> in the view of this context (the type's remarks, "Lookups"):
        /// from the nearest level whose layer of the type owns the id, else from the root. Each level costs one slot load
        /// and, where it has a layer of the type, the lock-free ownership test; only the owning level, or the root, is
        /// probed. Thread-safe.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type.</param>
        /// <param name="id">The container id.</param>
        /// <param name="container">The container, or <c>default</c>.</param>
        /// <returns><c>false</c> if no container of the type has the id in the view (for instance an id merged
        /// away).</returns>
        public bool TryGetContainer<TContainer>(ContainerEnv<TContainer> root, uint id, out TContainer container)
            where TContainer : struct, IContainerValue<TContainer>
        {
            // A level without a layer of the type holds none of its containers, not even under an id local to it, which
            // no level below holds either (no ancestor names an id local to a descendant), so it is skipped.
            for (var level = this; level is not null; level = level.parent)
                if (level.LayerOf(root) is { } layer && level.Owns(layer, id))
                    return layer.TryGetContainer(id, level.frozen, out container);

            return root.TryGetContainer(id, out container);
        }

        /// <summary>
        /// Gets the id of <paramref name="container"/>, whose content hash is <paramref name="hash"/>, in the view of this
        /// context (the type's remarks, "Lookups"), without interning it: the nearest level whose layer of the type holds
        /// the container under an id no level above it shadows, else the root under such an id. One probe per level with
        /// a non-empty layer of the type, plus the root's, and a shadow test per level above a hit. Thread-safe.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type.</param>
        /// <param name="container">The container.</param>
        /// <param name="hash">Its content hash (<see cref="IContainerValue{TSelf}.ComputeHash"/>).</param>
        /// <param name="id">Its id, or zero.</param>
        /// <returns><c>true</c> if the view holds the container.</returns>
        public bool TryGetId<TContainer>(ContainerEnv<TContainer> root, in TContainer container, ulong hash, out uint id)
            where TContainer : struct, IContainerValue<TContainer>
        {
            Debug.Assert(hash == container.ComputeHash(), "the content hash is stale");

            for (var level = this; level is not null; level = level.parent)
            {
                if (level.LayerOf(root) is not { IsEmpty: false } layer)
                    continue;

                if (layer.TryGetId(container, hash, level.frozen, out id) && ShadowedAbove(level, root.TypeId, id) == false)
                    return true;
            }

            if (root.TryGetId(container, hash, out id) && ShadowedAbove(null, root.TypeId, id) == false)
                return true;

            id = 0;
            return false;
        }

        /// <summary>
        /// Gets whether <paramref name="id"/> is a container's id in the view of this context, of any type the root's
        /// environments register: for each, the nearest level whose layer of the type owns the id answers, else the root.
        /// O(types × depth) lock-free tests and at most one probe per type. Thread-safe.
        /// </summary>
        /// <param name="values">The root's container environments.</param>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if a container of some type has the id.</returns>
        public bool IsContainerId(ContainerValues values, uint id)
        {
            var count = values.TypeCount;
            for (var i = 0; i < count; i++)
                if (ContainsId(values.GetEnvironment(ContainerTypeId.FromIndex(i)), id))
                    return true;

            return false;
        }

        /// <summary>
        /// Gets whether a container of the type <paramref name="root"/> holds has id <paramref name="id"/> in the view of
        /// this context, as <see cref="TryGetContainer{TContainer}"/> finds one, type-erased.
        /// </summary>
        bool ContainsId(ContainerEnvBase root, uint id)
        {
            for (var level = this; level is not null; level = level.parent)
                if (level.LayerAt(root.TypeId) is { } layer && level.Owns(layer, id))
                    return layer.ContainsId(id, level.frozen);

            return root.ContainsId(id);
        }

        /// <summary>
        /// Gets whether a level above <paramref name="below"/>, up to and including this context, shadows
        /// <paramref name="id"/> in its layer of the type: an entry under the id at <paramref name="below"/> (the root
        /// when <c>null</c>) is then hidden from this context. Lock-free; one shadow test per level with a layer of the
        /// type.
        /// </summary>
        bool ShadowedAbove(ContainerLayers? below, ContainerTypeId type, uint id)
        {
            for (var level = this; level != below && level is not null; level = level.parent)
                if (level.LayerAt(type) is { } layer && layer.Shadowed.Contains(id))
                    return true;

            return false;
        }

    }

}
