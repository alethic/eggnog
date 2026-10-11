using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The type-erased face of a <see cref="ContainerEnv{TContainer}"/>, through which <see cref="ContainerValues"/> holds
    /// environments of different container types: egglog's <c>DynamicContainerEnv</c> trait
    /// (core-relations/src/containers/mod.rs:280-298).
    /// </summary>
    /// <remarks>
    /// The members are what a rebuild needs without knowing the container type; each costs one virtual call per
    /// environment per rebuild. Typed access (interning, lookups) goes through the sealed generic class, reached by a
    /// cast once per call (egglog's <c>as_any().downcast_ref</c>).
    /// </remarks>
    internal abstract class ContainerEnvBase
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="typeId">The environment's id in its <see cref="ContainerValues"/>.</param>
        private protected ContainerEnvBase(ContainerTypeId typeId)
        {
            TypeId = typeId;
        }

        /// <summary>
        /// Gets the environment's id in its <see cref="ContainerValues"/>.
        /// </summary>
        public ContainerTypeId TypeId { get; }

        /// <summary>
        /// Gets the container type the environment holds.
        /// </summary>
        public abstract Type ContainerType { get; }

        /// <summary>
        /// Gets the number of containers. egglog's <c>to_id.len()</c>.
        /// </summary>
        public abstract int Count { get; }

        /// <summary>
        /// Rebuilds every container against <paramref name="rebuilder"/>: incrementally from the providing table's
        /// recent rows when <paramref name="recent"/> is given and few enough ids were displaced, otherwise by visiting
        /// every container. egglog's <c>apply_rebuild</c> (mod.rs:321-344). Requires exclusive access.
        /// </summary>
        /// <param name="table">The table providing the rebuilder (the union-find).</param>
        /// <param name="rebuilder">Its rebuilder.</param>
        /// <param name="recent">The provider's rows appended since the last rebuild, when the rebuilder has a hint
        /// column; otherwise <c>null</c>.</param>
        /// <param name="state">The state merge functions run in.</param>
        /// <returns>What changed.</returns>
        public abstract ContainerRebuildSummary ApplyRebuild(Table table, Rebuilder rebuilder, SubsetRef? recent, ExecutionState state);

        /// <summary>
        /// Adds to <paramref name="output"/> the ids of the containers of this environment that hold any of
        /// <paramref name="values"/>. egglog's <c>extend_containers_containing</c> (mod.rs:346-352), which climbs from a
        /// dirty container to the containers holding it.
        /// </summary>
        /// <param name="values">The values.</param>
        /// <param name="output">Receives the ids not already in it.</param>
        public abstract void ExtendContainersContaining(ReadOnlySpan<uint> values, OrderedValueSet output);

        /// <summary>
        /// Creates an independent copy that shares the (immutable) containers and the merge function. egglog's
        /// <c>dyn_clone</c>. Requires that nothing changes the environment meanwhile.
        /// </summary>
        /// <returns>The copy.</returns>
        public abstract ContainerEnvBase Clone();

        /// <summary>
        /// Checks the environment's internal invariants (the hash-cons, the reverse map and the element index agree
        /// exactly) and throws <see cref="InvalidOperationException"/> describing the first violation. For tests and
        /// debugging; O(size), allocates. Requires that nothing changes the environment meanwhile.
        /// </summary>
        public abstract void CheckInvariants();

    }

}
