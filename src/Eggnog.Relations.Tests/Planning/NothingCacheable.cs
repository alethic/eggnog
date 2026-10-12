using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A schema that answers as <paramref name="inner"/> does but says no column may be cached: what
    /// <see cref="PlanIndexKeys.Collect"/> must then list nothing for.
    /// </summary>
    /// <param name="inner">The schema answered from.</param>
    sealed class NothingCacheable(IQuerySchema inner) : IQuerySchema
    {

        /// <inheritdoc />
        public int GetArity(TableId table) => inner.GetArity(table);

        /// <inheritdoc />
        public int GetKeyCount(TableId table) => inner.GetKeyCount(table);

        /// <inheritdoc />
        public void SplitConstraints(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow) => inner.SplitConstraints(table, constraints, fast, slow);

        /// <inheritdoc />
        public bool IsCacheable(TableId table, ColumnId column) => false;

    }

}
