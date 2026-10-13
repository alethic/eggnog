using System;

namespace Eggnog.Relations
{

    internal sealed partial class ExecutionState
    {

        /// <summary>
        /// Gets the container environments of the database this state runs against: egglog's
        /// <c>ExecutionState::container_values</c> (core-relations/src/action/mod.rs:495-497), through which rule actions
        /// and external functions intern and read containers. Interning through it is immediate and global. In a child
        /// context this is the root's instance, which the child's database shares by reference, and container ids are
        /// minted on the store's shared counter, so they are never local to a child.
        /// </summary>
        /// <exception cref="InvalidOperationException">The state is not attached to a database, or the database is
        /// rebuilding its containers.</exception>
        public ContainerValues Containers => RequireView().Owner.Containers;

    }

}
