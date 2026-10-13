using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Records every value of a container under its id in a <see cref="ContainerValueIndex"/>: the loop
    /// <c>for val in container.iter() { val_index.entry(val).or_default().insert(id) }</c> of egglog's
    /// <c>get_or_insert</c> and <c>insert_owned</c> (core-relations/src/containers/mod.rs:394-396, 432-434).
    /// </summary>
    internal readonly struct ValueIndexAdder : IContainerElementVisitor
    {

        readonly ContainerValueIndex index;
        readonly uint id;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <param name="id">The container's id.</param>
        public ValueIndexAdder(ContainerValueIndex index, uint id)
        {
            this.index = index;
            this.id = id;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Visit(uint value) => index.Add(value, id);

    }

}
