namespace Eggnog
{

    /// <summary>
    /// An atom of a program over a table. Columns are the keys then the output; a relation has no output column here
    /// (lowering appends the unit constant). For a scoped atom, <see cref="Table"/> is the mirror's internal handle and
    /// <see cref="Scope"/> the tag, kept for the licence check and rendering (§6.3 step 2).
    /// </summary>
    /// <param name="Table">The table, or a scoped atom's mirror handle.</param>
    /// <param name="Scope">The tag a scoped atom reads; <c>null</c> for an atom over the running scope.</param>
    /// <param name="Columns">The operands of its columns.</param>
    internal sealed record ProgramAtom(TableHandle Table, ScopeTag? Scope, Operand[] Columns);

}
