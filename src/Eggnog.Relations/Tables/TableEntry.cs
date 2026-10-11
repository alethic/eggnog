namespace Eggnog.Relations
{

    /// <summary>
    /// An entry of a table's key index: egglog's <c>TableEntry</c> (core-relations/src/table/mod.rs:52-67). It holds the
    /// full 64-bit hash of a key and the id of the one live row with that key; the key itself stays in the row buffer
    /// and is compared there, never copied.
    /// </summary>
    /// <remarks>
    /// The stored hash is a cheap reject before the key comparison and lets the index grow without rehashing keys.
    /// </remarks>
    /// <param name="Hash">The full hash of the key (<see cref="TableKeyHash"/>).</param>
    /// <param name="Row">The row id of the live row with the key.</param>
    internal record struct TableEntry(ulong Hash, int Row);

}
