namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An unmanaged hash-table entry for tests: a key, a value, and the hash it was inserted with (stored, as egglog's
    /// table entries store theirs).
    /// </summary>
    /// <param name="Hash">The hash the entry was inserted with.</param>
    /// <param name="Key">The key.</param>
    /// <param name="Value">The value.</param>
    internal readonly record struct HashTestEntry(ulong Hash, int Key, int Value);

}
