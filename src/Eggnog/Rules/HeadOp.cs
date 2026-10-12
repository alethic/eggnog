namespace Eggnog
{

    /// <summary>
    /// A head operation of a program, lowered in order.
    /// </summary>
    /// <param name="Kind">What it does.</param>
    /// <param name="Table">The table of an insert, write or get-or-add.</param>
    /// <param name="Inputs">The operands: a row, a key, the two sides of a union, a computation's arguments, or the
    /// collected columns of a query or split matcher.</param>
    /// <param name="Target">The head register a get-or-add or computation defines; -1 otherwise.</param>
    /// <param name="Primitive">The primitive of a computation.</param>
    internal sealed record HeadOp(HeadOpKind Kind, TableHandle? Table, Operand[] Inputs, int Target, Primitive? Primitive);

}
