namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One action of a <see cref="ColorRule"/>'s head. Entries are encoded as in <see cref="ColorAtom"/>; a variable
    /// may be one an earlier action defined.
    /// </summary>
    /// <param name="Kind">What the action does.</param>
    /// <param name="Table">The table written, minted or read; -1 for a union.</param>
    /// <param name="Args">The entries: the logical row of a set, the two ids of a union, the key of a mint or a
    /// read.</param>
    /// <param name="Result">The variable a mint or a read defines; -1 otherwise.</param>
    readonly record struct ColorAction(ColorActionKind Kind, int Table, long[] Args, int Result);

}
