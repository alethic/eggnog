namespace Eggnog.Relations
{

    /// <summary>
    /// A copy of one row of a table and its row id: egglog's <c>Row</c> (core-relations/src/table_spec.rs:137-143), the
    /// result of a point lookup. Cold-path convenience: <see cref="Table.GetRow"/> allocates the copy. Hot paths use
    /// the non-allocating lookups, which copy into a caller's buffer or return a row id.
    /// </summary>
    /// <param name="Id">The row id, valid within the table's current generation.</param>
    /// <param name="Values">The row's cells; owned by the caller.</param>
    internal readonly record struct Row(int Id, uint[] Values);

}
