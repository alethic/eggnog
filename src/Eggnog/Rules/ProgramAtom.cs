namespace Eggnog
{

    /// <summary>
    /// An atom of a program over a table. Columns are the keys then the output; a relation has no output column here
    /// (lowering appends the unit constant).
    /// </summary>
    /// <param name="Table">The table.</param>
    /// <param name="Columns">The operands of its columns.</param>
    internal sealed record ProgramAtom(TableHandle Table, Operand[] Columns);

}
