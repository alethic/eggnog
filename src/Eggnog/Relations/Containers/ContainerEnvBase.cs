using System;
using System.Collections.Generic;

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
        /// <param name="holdsIds">Whether the containers hold e-class ids (eq-sort containers), which a child context
        /// interns and rebuilds in its own layers (<see cref="ContainerLayers"/>); <c>false</c> for containers over
        /// base values only.</param>
        private protected ContainerEnvBase(ContainerTypeId typeId, bool holdsIds)
        {
            TypeId = typeId;
            this.holdsIds = holdsIds;
        }

        /// <summary>
        /// Whether the containers hold e-class ids (<see cref="HoldsIds"/>). Volatile: <see cref="MarkHoldsIds"/> sets it
        /// while other threads may read it.
        /// </summary>
        volatile bool holdsIds;

        /// <summary>
        /// Gets whether the containers hold e-class ids: eq-sort containers, which a child context interns in its own
        /// container layers when its view does not hold them (<see cref="ContainerLayers.GetOrInsert{TContainer}"/>),
        /// and which a child that displaces one of their elements rebuilds there
        /// (<see cref="ContainerLayers.RebuildPass"/>). Containers over base values are registered with <c>holdsIds:
        /// false</c>, are allowed everywhere and never rebuild. One volatile read: a type registered over base values
        /// may come to hold ids (<see cref="MarkHoldsIds"/>) while other threads intern or look containers up.
        /// </summary>
        public bool HoldsIds => holdsIds;

        /// <summary>
        /// Gets the container log of the level this environment belongs to, which it records every container of classes
        /// that appears in it into while the log is active (<see cref="ContainerLog"/>): the root's for the environments
        /// of the root's <see cref="ContainerValues"/> that hold e-class ids, a child's for its layers' own environments;
        /// <c>null</c> otherwise (and for an environment made outside either). Set by <see cref="ContainerLog.Attach"/>
        /// before the environment is published or holds ids, so a thread that reads <see cref="HoldsIds"/> set, or finds
        /// the environment, reads the log.
        /// </summary>
        public ContainerLog? Log { get; internal set; }

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
        /// Gets whether some container of this environment holds <paramref name="value"/>, through the exact element
        /// index: what a child's container pass asks of each level below it before it makes its first storage
        /// (<see cref="ContainerLayers.RebuildPass"/>). Thread-safe; one probe.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns><c>true</c> if a container holds it.</returns>
        public abstract bool AnyContainerMentions(uint value);

        /// <summary>
        /// Gets whether a container of this environment has id <paramref name="id"/>: the type-erased lookup by id that
        /// asks whether an id is a container's at all (<see cref="ContainerView.IsContainerId"/>). Thread-safe; one lock
        /// and one probe.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if a container has it.</returns>
        public abstract bool ContainsId(uint id);

        /// <summary>
        /// <see cref="ContainsId"/> without the shard lock, for an environment nothing writes any more, a frozen child's
        /// layer (<see cref="ContainerLayer{TContainer}"/>). One probe.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if a container has it.</returns>
        internal abstract bool ContainsIdUnlocked(uint id);

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
        /// Runs one child context's container pass over this type, this being the root's environment of it:
        /// <see cref="ContainerLayers.RebuildType{TContainer}"/>, reached through the type-erased environment, as a
        /// rebuild reaches each type. Domain thread of the child only.
        /// </summary>
        /// <param name="layers">The child's container layers, in their pass.</param>
        /// <param name="delta">The ids the child's journal gained since its last container pass.</param>
        /// <param name="ownLosers">Those of them the child displaced itself, not local to it.</param>
        /// <param name="summary">Receives the changes and the dirty ids.</param>
        internal abstract void RebuildLayer(ContainerLayers layers, ReadOnlySpan<uint> delta, ReadOnlySpan<uint> ownLosers, ContainerRebuildSummary summary);

        /// <summary>
        /// Classifies <paramref name="entry"/>, an Added entry of the container log of the ancestor at depth
        /// <paramref name="depth"/> of the child context whose layers are <paramref name="layers"/>, this being the
        /// root's environment of the entry's type: <see cref="ContainerLayers.ClassifyAdded{TContainer}"/>, reached
        /// through the type-erased environment, as a rebuild reaches each type. Changes nothing. Domain thread of the
        /// child only.
        /// </summary>
        /// <param name="layers">The child's container layers.</param>
        /// <param name="depth">The ancestor's depth.</param>
        /// <param name="entry">The entry.</param>
        /// <param name="rebuilder">The child's union-find canonicalizer.</param>
        /// <returns>The entry's class.</returns>
        internal abstract ContainerCatchUpClass ClassifyAdded(ContainerLayers layers, int depth, ContainerLogEntry entry, Rebuilder rebuilder);

        /// <summary>
        /// Settles <paramref name="entry"/>, an Added entry of the ancestor at depth <paramref name="depth"/> whose
        /// content the child's layer of its type holds under another id, this being the root's environment of the
        /// entry's type: <see cref="ContainerLayers.SettleCollision{TContainer}"/>, type-erased. Domain thread of the
        /// child only, in the first pass of its rebuild.
        /// </summary>
        /// <param name="layers">The child's container layers.</param>
        /// <param name="depth">The ancestor's depth.</param>
        /// <param name="entry">The entry.</param>
        /// <param name="state">The state the merge stages its union in.</param>
        internal abstract void SettleCollision(ContainerLayers layers, int depth, ContainerLogEntry entry, ExecutionState state);

        /// <summary>
        /// Moves the Added entries this environment buffered for <see cref="Log"/> to <paramref name="destination"/>,
        /// each shard's buffer swapped out under that shard's lock. Called by <see cref="ContainerLog.Drain"/> under the
        /// log's lock.
        /// </summary>
        /// <param name="destination">Receives the entries, shard by shard.</param>
        internal abstract void DrainLogBuffers(List<ContainerLogEntry> destination);

        /// <summary>
        /// Discards the Added entries this environment buffered for <see cref="Log"/>, each shard's under that shard's
        /// lock. Called by <see cref="ContainerLog.Clear"/> under the log's lock.
        /// </summary>
        internal abstract void DiscardLogBuffers();

        /// <summary>
        /// Gets the number of Added entries this environment has buffered for <see cref="Log"/> and not drained, counted
        /// under each shard's lock. For the invariant checker.
        /// </summary>
        internal abstract int BufferedLogEntries { get; }

        /// <summary>
        /// Makes the environment one that holds e-class ids, recording into <paramref name="log"/> from then on: for a
        /// container type registered over base values first and later over an eq sort, as egglog keeps both in one
        /// environment, telling them apart by <c>do_rebuild</c> (<see cref="IContainerValue{TSelf}.HoldsIds"/>). Its
        /// containers so far hold no ids, so no child holds a displacement of theirs, and none is a container of classes
        /// the log should have recorded. Called by <see cref="ContainerValues.RegisterType{TContainer}"/> under its
        /// lock; other threads may read <see cref="HoldsIds"/> meanwhile.
        /// </summary>
        /// <param name="log">The container log of the <see cref="ContainerValues"/> holding it.</param>
        internal void MarkHoldsIds(ContainerLog log)
        {
            // The log first: the volatile write of the flag publishes it, so a thread that reads the flag set records
            // into the log.
            log.Attach(this);
            holdsIds = true;
        }

    }

}
