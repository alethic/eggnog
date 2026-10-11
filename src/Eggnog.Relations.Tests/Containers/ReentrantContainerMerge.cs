using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A merge that reaches back into the database's containers through its state, which a container rebuild must not
    /// allow (egglog takes the containers out of the database while it rebuilds them).
    /// </summary>
    sealed class ReentrantContainerMerge : ContainerMergeFunction
    {

        /// <inheritdoc />
        public override uint Merge(ExecutionState state, uint current, uint incoming)
        {
            _ = state.Containers;
            return Math.Min(current, incoming);
        }

    }

}
