using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Moves every value of a container from its old id to its new id in a <see cref="ContainerValueIndex"/>: the loop
    /// of <c>swap_remove(old)</c> and <c>insert(result)</c> in egglog's merge (core-relations/src/containers/mod.rs:422-426,
    /// 662-666), also run for the id-only renames of a full rebuild, which egglog leaves stale (mod.rs:553-558, 618-623).
    /// </summary>
    internal readonly struct ValueIndexRenamer : IContainerElementVisitor
    {

        readonly ContainerValueIndex index;
        readonly uint oldId;
        readonly uint newId;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <param name="oldId">The container's old id.</param>
        /// <param name="newId">The container's new id.</param>
        public ValueIndexRenamer(ContainerValueIndex index, uint oldId, uint newId)
        {
            this.index = index;
            this.oldId = oldId;
            this.newId = newId;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Visit(uint value) => index.Rename(value, oldId, newId);

    }

}
