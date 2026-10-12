using System;
using System.Runtime.CompilerServices;
using System.Threading;

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
        /// <param name="holdsIds">Whether the containers hold e-class ids (eq-sort containers), which colored contexts
        /// refuse to intern in a child (decision 9, G-a); <c>false</c> for containers over base values only.</param>
        private protected ContainerEnvBase(ContainerTypeId typeId, bool holdsIds)
        {
            TypeId = typeId;
            HoldsIds = holdsIds;
        }

        /// <summary>
        /// The eq-container version of the <see cref="ContainerValues"/> holding this environment, when the environment
        /// holds e-class ids; <c>null</c> otherwise (and for an environment made outside a <see cref="ContainerValues"/>).
        /// </summary>
        StrongBox<long>? eqVersion;

        /// <summary>
        /// Gets whether the containers hold e-class ids (colored-contexts spec §10.7): eq-sort containers, which a child
        /// context may look up but not intern (G-a), and whose elements a child may not displace (G-b, §7.4). Containers
        /// over base values are registered with <c>holdsIds: false</c> and are allowed everywhere.
        /// </summary>
        public bool HoldsIds { get; }

        /// <summary>
        /// Gets or sets the counter this environment increments each time it gains a container whose content it did not
        /// hold before (<see cref="ContainerValues.EqVersion"/>, §7.4 G-b): set by <see cref="ContainerValues"/> on the
        /// environments that hold e-class ids, before any container is interned.
        /// </summary>
        internal StrongBox<long>? EqVersionCounter
        {
            get => eqVersion;
            set => eqVersion = value;
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
        /// Gets whether some container of this environment holds <paramref name="value"/>, through the exact element index
        /// (colored-contexts spec §7.4, G-b). Thread-safe; one probe.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns><c>true</c> if a container holds it.</returns>
        public abstract bool AnyContainerMentions(uint value);

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

        /// <summary>
        /// Records that the environment gained a container whose content it did not hold before: moves
        /// <see cref="EqVersionCounter"/>, when set, so every child context re-runs its eq-container guard over its own
        /// displacements before it is read again (§7.4, G-b). One interlocked increment per new container of a type that
        /// holds ids; nothing otherwise. Thread-safe.
        /// </summary>
        private protected void NoteNewContent()
        {
            if (eqVersion is { } version)
                Interlocked.Increment(ref version.Value);
        }

    }

}
