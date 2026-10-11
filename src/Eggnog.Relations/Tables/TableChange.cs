namespace Eggnog.Relations
{

    /// <summary>
    /// What a <see cref="Table.Merge"/> did: egglog's <c>TableChange</c> (core-relations/src/table_spec.rs:83-90).
    /// </summary>
    /// <remarks>
    /// <see cref="Added"/> is true only when a row was really appended: a new key, or a merge function that returned
    /// <c>true</c>. Merges that leave every row as it was report nothing, in the parallel path too (egglog's parallel
    /// insert reports every staged row, core-relations/src/table/mod.rs:871; map §8.1 row 4).
    /// </remarks>
    /// <param name="Added">Whether rows were added (or, for the union-find, updated in place).</param>
    /// <param name="Removed">Whether rows were removed (or, for the union-find, updated in place).</param>
    internal readonly record struct TableChange(bool Added, bool Removed);

}
