using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal sealed partial class Database
    {

        /// <summary>
        /// Sorts an atom's constraints on <paramref name="table"/> into fast and slow ones and computes the rows
        /// matching the fast ones. egglog's <c>process_constraints</c> (core-relations/src/free_join/mod.rs:718-761).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="constraints">The constraints.</param>
        /// <returns>The result, which the caller owns and disposes.</returns>
        /// <exception cref="KeyNotFoundException">No such table.</exception>
        public ProcessedConstraints ProcessConstraints(TableId table, ReadOnlySpan<Constraint> constraints)
        {
            var fast = new List<Constraint>(constraints.Length);
            var slow = new List<Constraint>(constraints.Length);
            var subset = ProcessConstraints(table, constraints, fast, slow);
            return new ProcessedConstraints(subset, fast.ToArray(), slow.ToArray());
        }

        /// <summary>
        /// Sorts an atom's constraints on <paramref name="table"/> into fast and slow ones, appending them to
        /// <paramref name="fast"/> and <paramref name="slow"/>, and returns the rows matching the fast ones. egglog's
        /// <c>process_constraints</c> (core-relations/src/free_join/mod.rs:718-761). Safe from any number of threads
        /// while nothing merges.
        /// </summary>
        /// <remarks>
        /// <para>
        /// First the table answers what it can (<see cref="Table.SplitFastSlow"/>: ranges on its sorted column, a key
        /// lookup on the union-find). Then every remaining <see cref="ConstraintKind.EqConst"/> on a cacheable column is
        /// promoted to fast: the subset is intersected with the rows the database's cached column index lists under
        /// the constant, or emptied when the constant does not occur; the index is built on first use, O(rows) once.
        /// Promoted constraints follow the table's own fast ones. Repeated-variable <see cref="ConstraintKind.Eq"/>s
        /// and other comparisons stay slow, in their order.
        /// </para>
        /// <para>
        /// The subset may include tombstones (the table's fast subsets and the column index list rows deleted since
        /// they were indexed); scans through the table filter them.
        /// </para>
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <param name="constraints">The constraints.</param>
        /// <param name="fast">Receives the fast constraints.</param>
        /// <param name="slow">Receives the slow constraints.</param>
        /// <returns>The rows matching every fast constraint, which the caller owns.</returns>
        /// <exception cref="KeyNotFoundException">No such table.</exception>
        public Subset ProcessConstraints(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow)
        {
            ArgumentNullException.ThrowIfNull(fast);
            ArgumentNullException.ThrowIfNull(slow);

            var info = GetTableInfo(table);
            var firstSlow = slow.Count;
            var subset = info.Table.SplitFastSlow(constraints, fast, slow);

            // egglog's slow.retain (mod.rs:725-759), compacting in place.
            var kept = firstSlow;
            for (var i = firstSlow; i < slow.Count; i++)
            {
                var constraint = slow[i];
                var column = new ColumnId(constraint.Column);
                if (constraint.Kind != ConstraintKind.EqConst || info.Spec.IsUncacheable(column))
                {
                    slow[kept++] = constraint;
                    continue;
                }

                fast.Add(constraint);
                var index = info.GetColumnIndex(column);
                if (index.Data.TryGetSubset(constraint.Constant, out var rows))
                {
                    subset.Intersect(rows);
                }
                else
                {
                    // No row holds the constant: nothing matches.
                    subset.Dispose();
                    subset = Subset.Empty;
                }
            }

            slow.RemoveRange(kept, slow.Count - kept);
            return subset;
        }

    }

}
