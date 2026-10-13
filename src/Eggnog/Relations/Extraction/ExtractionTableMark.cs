namespace Eggnog.Relations
{

    /// <summary>
    /// What an <see cref="ExtractionWatch"/> records of one table at one level of a context chain: enough to tell, without
    /// the clock, whether the rows that level contributes to the table changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Within one <see cref="Generation"/>, a table's rows only grow (<see cref="Rows"/>, the physical row count,
    /// tombstones included) and lose rows only to tombstones (<see cref="Live"/> drops), so any insert, merge, rewrite or
    /// delete moves <see cref="Rows"/> or <see cref="Live"/>; a compaction or a clear moves <see cref="Generation"/>.
    /// <see cref="Structure"/> is a child layer's <see cref="TableLayer.Version"/>, moved by every hide and clear of
    /// ancestor rows. A context that has no layer of the table records <c>default</c>, which is also what a layer created
    /// since and still empty records, so a write that created a layer and merged nothing into it is not a change.
    /// </para>
    /// <para>
    /// Immutable. Equality is member-wise.
    /// </para>
    /// </remarks>
    /// <param name="Generation">The table's <see cref="TableVersion.Major"/>.</param>
    /// <param name="Rows">The table's <see cref="TableVersion.Minor"/>: its physical row count.</param>
    /// <param name="Live">The table's live row count (<see cref="Table.Count"/>).</param>
    /// <param name="Structure">A child layer's structure version (<see cref="TableLayer.Version"/>); 0 at the root.</param>
    internal readonly record struct ExtractionTableMark(int Generation, int Rows, int Live, int Structure);

}
