using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Stages one row per live lane into a table's mutation buffer: the loop of egglog's <c>Instr::Insert</c>
    /// (core-relations/src/action/mod.rs:774-780). The buffer is resolved once per batch, and the caller notifies the
    /// table and sets the state's changed flag once, where egglog does both per row.
    /// </summary>
    internal readonly struct StageInsertVisitor : ILaneVisitor
    {

        readonly MutationBuffer buffer;
        readonly ArgGather row;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="buffer">The table's buffer.</param>
        /// <param name="row">Gathers the row.</param>
        public StageInsertVisitor(MutationBuffer buffer, ArgGather row)
        {
            this.buffer = buffer;
            this.row = row;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Visit(int lane)
        {
            row.Load(lane);
            buffer.StageInsert(row.Values);
        }

    }

}
