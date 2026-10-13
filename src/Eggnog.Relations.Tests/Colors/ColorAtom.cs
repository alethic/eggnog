namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One atom of a <see cref="ColorRule"/>'s body: a table of <see cref="ColorSchema"/> and one entry per logical column
    /// (keys, then the output of a function; a relation's unit output is left out). An entry is a variable index
    /// (non-negative) or a constant <c>c</c> encoded as <c>-1 - c</c> (<see cref="ColorRule.Constant"/>).
    /// </summary>
    /// <param name="Table">The table's index in <see cref="ColorSchema.Tables"/>.</param>
    /// <param name="Entries">The entries, one per logical column.</param>
    readonly record struct ColorAtom(int Table, long[] Entries);

}
