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
        /// A class constant, by its index in <see cref="RuleProgram.WrittenIdConstants"/>: lowered with the constant
        /// values its variant is built with (<see cref="LoweredRule.Constants"/>).
        /// </summary>
        IdConstant,

    }

}
