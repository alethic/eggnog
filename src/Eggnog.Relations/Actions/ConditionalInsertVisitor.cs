using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Stages one row per live lane in which two operands are equal: the loops of egglog's <c>Instr::InsertIfEq</c>
    /// (core-relations/src/action/mod.rs:781-811) for every operand shape but two constants. Counts what it stages, so
    /// the caller notifies the table and sets the changed flag only if a row was staged.
    /// </summary>
    internal struct ConditionalInsertVisitor : ILaneVisitor
    {

        readonly MutationBuffer buffer;
        readonly ArgGather row;
        readonly ArgSource left;
        readonly ArgSource right;
        int staged;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="buffer">The table's buffer.</param>
        /// <param name="row">Gathers the row.</param>
        /// <param name="left">The first operand compared.</param>
        /// <param name="right">The second operand compared.</param>
        public ConditionalInsertVisitor(MutationBuffer buffer, ArgGather row, ArgSource left, ArgSource right)
        {
            this.buffer = buffer;
            this.row = row;
            this.left = left;
            this.right = right;
            staged = 0;
        }

        /// <summary>
        /// Gets the number of rows staged.
        /// </summary>
        public readonly int Staged => staged;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Visit(int lane)
        {
            if (left.Get(lane) != right.Get(lane))
                return;

            row.Load(lane);
            buffer.StageInsert(row.Values);
            staged++;
        }

    }

}
