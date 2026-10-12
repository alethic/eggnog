using System;

namespace Eggnog.Relations
{

    internal sealed partial class ContextTables
    {

        /// <summary>
        /// Clears <paramref name="table"/> in the context (§6.7): its own rows are removed and, for a shared table, every
        /// ancestor layer becomes absent from its views, so the context (and the descendants it creates later) sees the
        /// table empty until it writes it again.
        /// </summary>
        /// <remarks>
        /// The retraction guard refuses the clear of a shared table while the context has live descendants or its parent
        /// is not frozen (decision 1, R1); the layer is created if needed (decision 2 applies). The own table moves to a
        /// new generation only (no descendant reads it); its indexes are reset, the change stamp moves, hidden sets are
        /// sealed and every view is dropped. Domain thread only.
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <exception cref="InvalidOperationException">The clear is refused (§6.7, decision 2), or the table is the
        /// union-find, whose rows are never deleted.</exception>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">The table is not registered.</exception>
        internal void ClearTable(TableId table)
        {
            if (table == schema.UnionFind)
                throw new InvalidOperationException($"The union-find ({Store.UnionFindName}) of a child context cannot be cleared: its displacements are never retracted (decision 1).");

            guard.CheckClear(table);
            var layer = GetOrCreateLayer(table);
            owner.Database.ClearOwnLayer(layer.Info);
            if (layer.Schema.IsContextPrivate == false)
                layer.SetCleared();

            MarkDirty(table);
            SealHidden();
            InvalidateViews(true);
        }

    }

}
