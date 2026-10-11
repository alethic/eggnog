namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the live lanes where <see cref="Left"/> differs from <see cref="Right"/>. egglog's <c>Instr::AssertNe</c>
    /// (core-relations/src/action/mod.rs:971-972, 874). Two constants are compared once.
    /// </summary>
    internal sealed class AssertNeInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="left">The first operand.</param>
        /// <param name="right">The second operand.</param>
        public AssertNeInstr(QueryEntry left, QueryEntry right)
            : base(InstrKind.AssertNe)
        {
            Left = CheckOperand(left, nameof(left));
            Right = CheckOperand(right, nameof(right));
        }

        /// <summary>
        /// Gets the first operand.
        /// </summary>
        public QueryEntry Left { get; }

        /// <summary>
        /// Gets the second operand.
        /// </summary>
        public QueryEntry Right { get; }

        /// <inheritdoc />
        public override string ToString() => "AssertNe " + Left + " " + Right;

    }

}
