using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Reports every container of a layer's own environment held under an id the layer may not own: one neither local to
    /// the context nor shadowed by the layer (<see cref="ContainerLayer{TContainer}"/>, the ownership rule). For the
    /// invariant checker.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal readonly struct ContainerOwnershipChecker<TContainer> : IContainerVisitor<TContainer>
        where TContainer : struct, IContainerValue<TContainer>
    {

        readonly ContainerLayers owner;
        readonly ShadowSet shadowed;
        readonly ContainerTypeId type;
        readonly List<string> violations;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="owner">The layers of the context the layer belongs to.</param>
        /// <param name="shadowed">The ids the layer shadows.</param>
        /// <param name="type">The layer's container type.</param>
        /// <param name="violations">Receives one line per violation.</param>
        public ContainerOwnershipChecker(ContainerLayers owner, ShadowSet shadowed, ContainerTypeId type, List<string> violations)
        {
            this.owner = owner;
            this.shadowed = shadowed;
            this.type = type;
            this.violations = violations;
        }

        /// <inheritdoc />
        public void Visit(in TContainer container, uint id)
        {
            if (owner.IsLocal(id) == false && shadowed.Contains(id) == false)
                Context.Report(violations, $"container layers: type {type} holds a container under {id}, which is neither local to the context nor shadowed");
        }

    }

}
