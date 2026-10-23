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
        /// through it is immediate and global, in the root's environments (<see cref="ContainerView.GetOrInsert"/>), and
        /// container ids are minted on the store's shared counter, so they are never local to a child.
        /// </summary>
        /// <exception cref="InvalidOperationException">The state is not attached to a database, or the database is
        /// rebuilding its containers.</exception>
        public ContainerView Containers => ContainerView.Of(RequireView().Owner);

    }

}
