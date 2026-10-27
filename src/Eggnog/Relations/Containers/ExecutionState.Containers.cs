using System;

namespace Eggnog.Relations
{

    internal sealed partial class ExecutionState
    {

        /// <summary>
        /// Gets the containers of the context whose database this state runs against: egglog's
        /// <c>ExecutionState::container_values</c> (core-relations/src/action/mod.rs:495-497), through which rule actions
        /// and external functions intern and read containers, as that context's view (<see cref="ContainerView"/>): at a
        /// root, the root's environments; in a child, its container layers and its ancestors' over them. Interning
        /// through it is immediate (<see cref="ContainerView.GetOrInsert{TContainer}(in TContainer, ExecutionState)"/>):
        /// a container of values in the root's environments, under an id minted on the store's shared counter and so the
        /// same in every context; a container of classes at a writable root in its environments, and in a writable
        /// child in its own container layers, under an id minted from its id blocks and so local to it
        /// (<see cref="ContainerLayers.GetOrInsert{TContainer}"/>).
        /// </summary>
        /// <exception cref="InvalidOperationException">The state is not attached to a database, or the database is
        /// rebuilding its containers.</exception>
        public ContainerView Containers => ContainerView.Of(RequireView().Owner);

    }

}
