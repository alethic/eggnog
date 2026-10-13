using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Stages one removal per live lane into a table's mutation buffer: the loop of egglog's <c>Instr::Remove</c>
    /// (core-relations/src/action/mod.rs:812-818). The buffer is resolved once per batch, and the caller notifies the
    /// table and sets the state's changed flag once, where egglog does both per key.
    /// </summary>
    internal readonly struct StageRemoveVisitor : ILaneVisitor
    {

        readonly MutationBuffer buffer;
        readonly ArgGather key;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="buffer">The table's buffer.</param>
        /// <param name="key">Gathers the key.</param>
        public StageRemoveVisitor(MutationBuffer buffer, ArgGather key)
        {
            this.buffer = buffer;
            this.key = key;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Visit(int lane)
        {
            key.Load(lane);
            buffer.StageRemove(key.Values);
        }

    }

}
