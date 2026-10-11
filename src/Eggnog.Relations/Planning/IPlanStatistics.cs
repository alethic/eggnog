namespace Eggnog.Relations
{

    /// <summary>
    /// The statistics the planner reads: egglog's <c>ProcessedConstraints::approx_size</c> and
    /// <c>ColumnCardEst::col_uniqueness</c> (core-relations/src/free_join/mod.rs:91-94, 878-896).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Planning is a cold path, so this is an ordinary interface. A database implements it over its tables (subset
    /// sizes after the fast constraints, distinct counts from cached column indexes); tests implement it with fixed
    /// numbers, which makes plans reproducible.
    /// </para>
    /// <para>
    /// Data only steers choices (Free Join cover order, set-cover ties, the variable-elimination order); the stage
    /// lists never embed data, so a template planned once can be reused in every run and every context.
    /// </para>
    /// </remarks>
    internal interface IPlanStatistics
    {

        /// <summary>
        /// Gets the approximate number of rows of the atom's table that match its fast constraints. egglog's
        /// <c>approx_size</c>, the size of the atom's header subset when the query was built.
        /// </summary>
        /// <param name="atom">The atom's identifier.</param>
        /// <param name="info">The atom.</param>
        /// <returns>The estimate; never negative.</returns>
        long GetApproximateSize(AtomId atom, QueryAtom info);

        /// <summary>
        /// Gets the uniqueness estimate for <paramref name="column"/> of <paramref name="table"/>: the table's row count
        /// and the column's distinct-value count. egglog's <c>col_uniqueness</c>, which uses whole-table numbers, not
        /// the atom's subset.
        /// </summary>
        /// <param name="table">The table; never <see cref="TableId.Dummy"/>.</param>
        /// <param name="column">The column.</param>
        /// <returns>The estimate.</returns>
        ColumnUniqueness GetColumnUniqueness(TableId table, ColumnId column);

    }

}
