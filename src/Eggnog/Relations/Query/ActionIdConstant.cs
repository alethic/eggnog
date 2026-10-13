namespace Eggnog.Relations
{

    /// <summary>
    /// A constant e-class id in a rule's action program (colored-contexts spec §9.8, X19): operand
    /// <see cref="Operand"/> of instruction <see cref="Instr"/> is the constant <see cref="Value"/>, and it is an id,
    /// because it lands in an id column of the instruction's table (or of the store's union-find) or because the rule's
    /// author flagged it (<see cref="QueryEntry.IdConst"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A store-backed context runs such a constant in its canonical form there, as it does the constants of the query
    /// (<see cref="IdConstant"/>), so a rule writes and compares what a copy of the context would
    /// (<see cref="CanonicalConstants"/>, <see cref="ActionSubstituter"/>). A bare database runs it as written.
    /// </para>
    /// <para>
    /// <see cref="Operand"/> numbers the instruction's operands in the order <see cref="ActionSubstituter"/> defines per
    /// <see cref="InstrKind"/>: the table operands first (keys, row values, or key then default entries), then the
    /// remaining comparands or function arguments.
    /// </para>
    /// </remarks>
    /// <param name="Instr">The instruction's index in the program.</param>
    /// <param name="Operand">The operand's index in the instruction (see <see cref="ActionSubstituter"/>).</param>
    /// <param name="Value">The constant, as written in the rule.</param>
    internal readonly record struct ActionIdConstant(int Instr, int Operand, Value Value)
    {

        /// <inheritdoc />
        public override string ToString() => $"instr {Instr}.{Operand} == {Value}";

    }

}
