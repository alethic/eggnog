using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// egglog's <c>process_constraints</c> (core-relations/src/free_join/mod.rs:718-761) on one table entry: the split of
    /// an atom's constraints into fast and slow ones and the rows matching the fast ones.
    /// <see cref="Database.ProcessConstraints(TableId, ReadOnlySpan{Constraint}, List{Constraint}, List{Constraint})"/>
    /// runs it on a database's own entry, and a layered context's view (<see cref="ContextView.Evaluate"/>) runs it once
    /// per present layer, each over that layer's own <see cref="TableInfo"/> and cached indexes.
    /// </summary>
    /// <remarks>
    /// Cold path: once per header per instantiation and once per atom when a rule is planned. Safe from any number of
    /// threads while the table does not merge.
    /// </remarks>
    internal static class ConstraintProcessor
    {

        /// <summary>
        /// Sorts <paramref name="constraints"/> on <paramref name="info"/>'s table into fast and slow ones, appending them
        /// to <paramref name="fast"/> and <paramref name="slow"/>, and returns the rows matching the fast ones: first the
        /// table answers what it can (<see cref="Table.SplitFastSlow"/>: ranges on its sorted column, a key lookup on the
        /// union-find); then every remaining <see cref="ConstraintKind.EqConst"/> on a cacheable column is promoted to
        /// fast, the subset intersected with the rows the entry's cached column index lists under the constant (built on
        /// first use, O(rows) once) or emptied when the constant does not occur. Promoted constraints follow the table's
        /// own fast ones; everything else stays slow, in order.
        /// </summary>
        /// <remarks>
        /// The subset may include tombstones (the table's fast subsets and the column index list rows deleted since they
        /// were indexed) and, in a layer of a view, rows the view hides; readers refine visibility where they read rows.
        /// </remarks>
        /// <param name="info">The table entry: a database's own, or one layer of a view.</param>
        /// <param name="constraints">The constraints.</param>
        /// <param name="fast">Receives the fast constraints.</param>
        /// <param name="slow">Receives the slow constraints.</param>
        /// <returns>The rows matching every fast constraint, which the caller owns.</returns>
        public static Subset Process(TableInfo info, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow)
        {
            ArgumentNullException.ThrowIfNull(info);
            ArgumentNullException.ThrowIfNull(fast);
            ArgumentNullException.ThrowIfNull(slow);

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

        /// <summary>
        /// Sorts <paramref name="constraints"/> exactly as <see cref="Process"/> does, without computing the rows the fast
        /// ones select and without building an index: the classification depends only on the table's kind, its sorted
        /// column and its cacheable columns, which every layer of a table shares. A child's
        /// <see cref="IQuerySchema.SplitConstraints"/> runs it on the root's entry.
        /// </summary>
        /// <param name="info">The table entry whose kind and spec classify.</param>
        /// <param name="constraints">The constraints.</param>
        /// <param name="fast">Receives the fast constraints.</param>
        /// <param name="slow">Receives the slow constraints.</param>
        public static void Split(TableInfo info, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow)
        {
            ArgumentNullException.ThrowIfNull(info);
            ArgumentNullException.ThrowIfNull(fast);
            ArgumentNullException.ThrowIfNull(slow);

            var table = info.Table;
            var firstSlow = slow.Count;
            foreach (var constraint in constraints)
            {
                // TryFastSubset's answer depends on the constraint and the table's sorted column only; the subset is a
                // range or empty, so disposing it releases nothing.
                if (table.TryFastSubset(constraint, out var sub))
                {
                    sub.Dispose();
                    fast.Add(constraint);
                }
                else
                {
                    slow.Add(constraint);
                }
            }

            var kept = firstSlow;
            for (var i = firstSlow; i < slow.Count; i++)
            {
                var constraint = slow[i];
                if (constraint.Kind != ConstraintKind.EqConst || info.Spec.IsUncacheable(new ColumnId(constraint.Column)))
                {
                    slow[kept++] = constraint;
                    continue;
                }

                fast.Add(constraint);
            }

            slow.RemoveRange(kept, slow.Count - kept);
        }

    }

}
