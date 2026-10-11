namespace Eggnog.Relations
{

    /// <summary>
    /// The version of a table: egglog's <c>TableVersion</c> (core-relations/src/table_spec.rs:41-51).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A new <see cref="Major"/> generation invalidates every row id, subset and index taken from the table: it changes
    /// when the table compacts (rehash) or when a nonempty table is cleared.
    /// </para>
    /// <para>
    /// Within a generation <see cref="Minor"/> is the physical row count, tombstones included, so the rows appended since
    /// an earlier version are exactly <c>[earlier.Minor, Minor)</c> (<see cref="Table.UpdatesSince"/>). Deleting rows
    /// changes neither number: readers of incrementally maintained subsets must filter tombstones themselves.
    /// </para>
    /// </remarks>
    /// <param name="Major">The generation; row ids are valid only within one.</param>
    /// <param name="Minor">The physical row count within the generation.</param>
    internal readonly record struct TableVersion(int Major, int Minor);

}
