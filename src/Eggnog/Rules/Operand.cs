namespace Eggnog
{

    /// <summary>
    /// An operand of a program: a register, a value constant's cell, or a class constant by index.
    /// </summary>
    /// <param name="Kind">What the operand is.</param>
    /// <param name="Index">The register's index, or the class constant's index in
    /// <see cref="RuleProgram.WrittenIdConstants"/>.</param>
    /// <param name="Type">The column type.</param>
    /// <param name="Bits">A value constant's cell.</param>
    internal readonly record struct Operand(OperandKind Kind, int Index, ColumnType Type, uint Bits)
    {

        /// <summary>
        /// A register operand.
        /// </summary>
        /// <param name="index">The register's index.</param>
        /// <param name="type">Its column type.</param>
        /// <returns>The operand.</returns>
        public static Operand Register(int index, ColumnType type) => new(OperandKind.Register, index, type, 0);

        /// <summary>
        /// A value constant operand.
        /// </summary>
        /// <param name="type">The column type.</param>
        /// <param name="bits">The value's cell.</param>
        /// <returns>The operand.</returns>
        public static Operand Constant(ColumnType type, uint bits) => new(OperandKind.Constant, -1, type, bits);

        /// <summary>
        /// A class constant operand.
        /// </summary>
        /// <param name="index">The constant's index in <see cref="RuleProgram.WrittenIdConstants"/>.</param>
        /// <param name="type">The sort.</param>
        /// <returns>The operand.</returns>
        public static Operand IdConstant(int index, ColumnType type) => new(OperandKind.IdConstant, index, type, 0);

        /// <summary>
        /// Gets whether the operand is a register.
        /// </summary>
        public bool IsRegister => Kind == OperandKind.Register;

    }

}
