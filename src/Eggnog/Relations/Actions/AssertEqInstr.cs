namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the live lanes where <see cref="Left"/> equals <see cref="Right"/>. egglog's <c>Instr::AssertEq</c>
    /// (core-relations/src/action/mod.rs:968-969, 873): primitive results checked against bound values. Two constants
    /// are compared once.
    /// </summary>
    internal sealed class AssertEqInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="left">The first operand.</param>
        /// <param name="right">The second operand.</param>
        public AssertEqInstr(QueryEntry left, QueryEntry right)
            : base(InstrKind.AssertEq)
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
        public override string ToString() => "AssertEq " + Left + " " + Right;

    }

}
