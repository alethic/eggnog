namespace Eggnog
{

    /// <summary>
    /// What an operand of a program is.
    /// </summary>
    internal enum OperandKind
    {

        /// <summary>
        /// A register, by index.
        /// </summary>
        Register,

        /// <summary>
        /// A value constant's cell.
        /// </summary>
        Constant,

        /// <summary>
        /// A class constant, by its index in <see cref="RuleProgram.WrittenIdConstants"/>: lowered with the cell a variant
        /// is built for (§6.4.5).
        /// </summary>
        IdConstant,

    }

}
