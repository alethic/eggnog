namespace Eggnog.Relations
{

    /// <summary>
    /// An entry of a <see cref="PredictedValues"/> index: one predicted row, stored in the cache's cell arena, with the
    /// table it belongs to and the full hash of (table, key).
    /// </summary>
    /// <param name="Hash">The 64-bit hash of the table id and the key.</param>
    /// <param name="Table">The table id's index.</param>
    /// <param name="Offset">The index of the row's first cell in the arena.</param>
    /// <param name="KeyLength">The number of key cells, which start the row.</param>
    /// <param name="RowLength">The number of cells of the row.</param>
    internal readonly record struct PredictedEntry(ulong Hash, int Table, int Offset, int KeyLength, int RowLength);

}
