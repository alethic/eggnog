using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The type-erased face of a <see cref="ContainerLayer{TContainer}"/>, through which <see cref="ContainerLayers"/>
    /// holds the layers of different container types, as <see cref="ContainerEnvBase"/> is for environments.
    /// </summary>
    internal abstract class ContainerLayerBase
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="typeId">The container type's id in the root's <see cref="ContainerValues"/>.</param>
        private protected ContainerLayerBase(ContainerTypeId typeId)
        {
            TypeId = typeId;
        }

        /// <summary>
        /// Gets the container type's id in the root's <see cref="ContainerValues"/>: the layer's slot in its
        /// <see cref="ContainerLayers"/>.
        /// </summary>
        public ContainerTypeId TypeId { get; }

        /// <summary>
        /// Gets the ancestor ids the layer shadows: every ancestor entry under one of them is hidden from the context and
        /// its descendants, and <see cref="OwnBase"/> holds what the context sees under it.
        /// </summary>
        public ShadowSet Shadowed { get; } = new();

        /// <summary>
        /// Gets the layer's own environment, type-erased.
        /// </summary>
        public abstract ContainerEnvBase OwnBase { get; }

        /// <summary>
        /// Gets whether the layer's own environment holds no container: a lookup by content skips the layer. A snapshot
        /// while the context's rule tasks insert.
        /// </summary>
        public abstract bool IsEmpty { get; }

        /// <summary>
        /// Gets whether the layer's own environment holds a container with id <paramref name="id"/>, taking its shard
        /// lock unless <paramref name="frozen"/>, when nothing writes it any more.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="frozen">Whether the owning context is frozen.</param>
        /// <returns><c>true</c> if a container has the id.</returns>
        public bool ContainsId(uint id, bool frozen) => frozen ? OwnBase.ContainsIdUnlocked(id) : OwnBase.ContainsId(id);

        /// <summary>
        /// Freezes the layer: its shadowed ids never change again, and the size of its own environment is stamped for
        /// the invariant checker. Domain thread only, as part of the owning context's freeze.
        /// </summary>
        public abstract void Freeze();

        /// <summary>
        /// Adds to <paramref name="violations"/> every way the layer breaks its invariants: its own environment's
        /// (<see cref="ContainerEnvBase.CheckInvariants"/>), the ownership rule (every id its own environment holds is
        /// local to the context or shadowed, and no shadowed id is local), and, once frozen, an own environment that
        /// changed size since the freeze. Requires that nothing changes the layer meanwhile.
        /// </summary>
        /// <param name="owner">The layers of the context the layer belongs to.</param>
        /// <param name="violations">Receives one line per violation.</param>
        public abstract void CheckInvariants(ContainerLayers owner, List<string> violations);

    }

}
