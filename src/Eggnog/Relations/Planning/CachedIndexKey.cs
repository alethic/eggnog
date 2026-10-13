using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One cached index a context builds while it freezes, before it is shared (an entry of a
    /// <see cref="FreezeWarmup"/>, collected from plans by <see cref="PlanIndexKeys.Collect"/>): a table and the ordered
    /// columns of the index, a column index for one column and a tuple index for more.
    /// </summary>
    /// <param name="Table">The table.</param>
    /// <param name="Columns">The key columns, in key order; at least one.</param>
    internal readonly record struct CachedIndexKey(TableId Table, ColumnId[] Columns)
    {

        /// <summary>
        /// Gets the key columns, checked: at least one.
        /// </summary>
        /// <exception cref="ArgumentException">The key has no column.</exception>
        public ReadOnlySpan<ColumnId> CheckedColumns => Columns is { Length: > 0 } columns ? columns : throw new ArgumentException("An index needs at least one key column.");

    }

}
