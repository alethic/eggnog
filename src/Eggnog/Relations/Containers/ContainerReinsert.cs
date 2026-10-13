namespace Eggnog.Relations
{

    /// <summary>
    /// A container a full rebuild took out of the hash-cons because its contents changed, waiting to be inserted back:
    /// egglog's <c>(C, Value, bool)</c> in <c>to_reinsert</c> (core-relations/src/containers/mod.rs:552, 617).
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal readonly struct ContainerReinsert<TContainer>
        where TContainer : struct, IContainerValue<TContainer>
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="container">The rebuilt container.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="id">The id to insert it under: its old id, rebuilt.</param>
        /// <param name="stable">Whether the id is the container's old id, so that a change of contents changes the
        /// meaning of that id.</param>
        public ContainerReinsert(TContainer container, ulong hash, uint id, bool stable)
        {
            Container = container;
            Hash = hash;
            Id = id;
            Stable = stable;
        }

        /// <summary>
        /// Gets the rebuilt container.
        /// </summary>
        public TContainer Container { get; }

        /// <summary>
        /// Gets its content hash.
        /// </summary>
        public ulong Hash { get; }

        /// <summary>
        /// Gets the id to insert it under.
        /// </summary>
        public uint Id { get; }

        /// <summary>
        /// Gets whether the id is the container's old id.
        /// </summary>
        public bool Stable { get; }

    }

}
