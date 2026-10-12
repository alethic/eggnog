namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// An atom of a reference body: a table and a slot per column (keys, then the output of a function or constructor).
    /// </summary>
    /// <param name="Table">The table.</param>
    /// <param name="Slots">The slots.</param>
    internal sealed record RefAtom(DiffTable Table, RefSlot[] Slots);

}
