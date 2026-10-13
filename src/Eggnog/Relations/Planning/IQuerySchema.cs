using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// What <see cref="QueryBuilder"/> needs to know about the tables an atom names: arities, key counts, and how a
    /// table classifies constraints into fast and slow ones.
    /// </summary>
    /// <remarks>
    /// The classification is schema-level in egglog: a constraint is fast when the table answers it with a subset
    /// (<c>Table::fast_subset</c>, a range on the sorted column) or when it is an <c>EqConst</c> on a cacheable column,
    /// which <c>Database::process_constraints</c> promotes through a column index (core-relations/src/free_join/mod.rs:718-761).
    /// Computing the subset itself is per run (<see cref="IHeaderEvaluator"/>). Cold path.
    /// </remarks>
    internal interface IQuerySchema
    {

        /// <summary>
        /// Gets the number of columns of <paramref name="table"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The arity.</returns>
        int GetArity(TableId table);

        /// <summary>
        /// Gets the number of key columns of <paramref name="table"/>; the key determines the remaining columns.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The key count.</returns>
        int GetKeyCount(TableId table);

        /// <summary>
        /// Sorts <paramref name="constraints"/> into the ones <paramref name="table"/> answers with a subset and the
        /// ones evaluated per row, in egglog's order: first the table's own fast constraints, then promoted equalities.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="constraints">The constraints.</param>
        /// <param name="fast">Receives the fast constraints.</param>
        /// <param name="slow">Receives the slow constraints.</param>
        void SplitConstraints(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow);

        /// <summary>
        /// Gets whether the database may keep a cached index over <paramref name="column"/> of <paramref name="table"/>
        /// (<see cref="TableSpec.IsUncacheable"/>): what <see cref="PlanIndexKeys.Collect"/> lists for a frozen context to
        /// prebuild. Every column, unless the schema says otherwise.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="column">The column.</param>
        /// <returns><c>true</c> if a cached index over the column may be built.</returns>
        bool IsCacheable(TableId table, ColumnId column) => true;

    }

}
