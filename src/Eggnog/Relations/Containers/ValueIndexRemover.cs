using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Removes every value of a container, recorded under its id, from a <see cref="ContainerValueIndex"/>: what a
    /// container leaving the hash-cons does to keep the index exact. egglog never removes from its index, which goes
    /// stale after a rebuild that only renames a container (core-relations/src/containers/mod.rs:553-558, 618-623).
    /// </summary>
    internal readonly struct ValueIndexRemover : IContainerElementVisitor
    {

        readonly ContainerValueIndex index;
        readonly uint id;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <param name="id">The container's id.</param>
        public ValueIndexRemover(ContainerValueIndex index, uint id)
        {
            this.index = index;
            this.id = id;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Visit(uint value) => index.Remove(value, id);

    }

}
