namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A hash-table entry that holds a reference, to exercise the slot clearing a table does for such entries.
    /// </summary>
    /// <param name="Hash">The hash the entry was inserted with.</param>
    /// <param name="Key">The key.</param>
    internal readonly record struct HashTestRefEntry(ulong Hash, string Key);

}
