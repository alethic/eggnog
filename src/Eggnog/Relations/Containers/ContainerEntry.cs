namespace Eggnog.Relations
{

    /// <summary>
    /// One container of a <see cref="ContainerEnv{TContainer}"/> with its content hash and id: the slot type of both of
    /// the environment's hash tables, the hash-cons keyed by content (egglog's <c>to_id: DashMap&lt;C, Value&gt;</c>) and
    /// the reverse map keyed by id (egglog's <c>to_container</c>).
    /// </summary>
    /// <remarks>
    /// egglog's reverse map stores only the content hash and the hash-cons shard, because a second copy of a Rust
    /// container would duplicate its heap storage; a lookup by id then probes the hash-cons shard under a read lock it
    /// takes twice (mod.rs:701-708). Containers here are immutable structs whose copies share their storage, so the
    /// reverse map holds the container itself and a lookup by id takes one lock and one probe. The <see cref="Hash"/>
    /// is kept so neither table recomputes it when it grows, and so an entry can be found in the hash-cons again from
    /// the reverse map.
    /// </remarks>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal struct ContainerEntry<TContainer>
        where TContainer : struct, IContainerValue<TContainer>
    {

        /// <summary>
        /// The container.
        /// </summary>
        public TContainer Container;

        /// <summary>
        /// The container's content hash, <see cref="IContainerValue{TSelf}.ComputeHash"/>.
        /// </summary>
        public ulong Hash;

        /// <summary>
        /// The container's id. A rebuild may change it in place in the hash-cons (the content, and so the slot, stays).
        /// </summary>
        public uint Id;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="id">Its id.</param>
        public ContainerEntry(TContainer container, ulong hash, uint id)
        {
            Container = container;
            Hash = hash;
            Id = id;
        }

    }

}
