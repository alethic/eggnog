using System;

namespace Eggnog.Relations
{

    internal sealed partial class ExecutionState
    {

        /// <summary>
        /// Gets the container environments of the database this state runs against: egglog's
        /// <c>ExecutionState::container_values</c> (core-relations/src/action/mod.rs:495-497), through which rule actions
        /// and external functions intern and read containers. Interning through it is immediate and global.
        /// </summary>
        /// <exception cref="InvalidOperationException">The state is not attached to a database, or the database is
        /// rebuilding its containers.</exception>
        public ContainerValues Containers => RequireView().Owner.Containers;

    }

}
