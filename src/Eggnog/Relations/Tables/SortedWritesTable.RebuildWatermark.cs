using System;

namespace Eggnog.Relations
{

    internal sealed partial class SortedWritesTable
    {

        /// <summary>
        /// For each rebuilder, keyed by its table id as <see cref="subsetTracker"/> keys the union-find positions the mark
        /// is paired with: this table's version when <see cref="ApplyRebuild"/> last ran against that rebuilder. The rows
        /// below <see cref="TableVersion.Minor"/> in generation <see cref="TableVersion.Major"/> were canonical as of that
        /// rebuild's view of that rebuilder (or were staged for rewriting by it, and are tombstones once the rewrite
        /// merged). A rebuild against one rebuilder says nothing about another one's ids, so each keeps its own mark. A
        /// new generation (compaction, clearing) renumbers the rows, which voids every mark. Created on the first
        /// rebuild; a clone starts over, as its rebuild bookkeeping does.
        /// </summary>
        DenseIdMap<TableId, TableVersion>? rebuiltAt;

        /// <summary>
        /// Gets the first row appended since <see cref="ApplyRebuild"/> last ran against
        /// <paramref name="rebuilderTableId"/>: rows at and after it may mention ids that rebuilder displaced before that
        /// rebuild, which its recent updates do not name. Zero before the first rebuild against it and after a new
        /// generation. O(1).
        /// </summary>
        /// <param name="rebuilderTableId">The rebuilder's table id.</param>
        /// <returns>The first row written since that rebuild.</returns>
        internal int FirstRowSinceRebuild(TableId rebuilderTableId)
        {
            return rebuiltAt is not null && rebuiltAt.TryGetValue(rebuilderTableId, out var at) && at.Major == generation
                ? Math.Min(at.Minor, data.Count)
                : 0;
        }

        /// <summary>
        /// Records that the table rebuilt against <paramref name="rebuilderTableId"/>: every row now present is canonical
        /// as of this rebuild's view of that rebuilder once its staged rewrites merge (they append rows, which the next
        /// rebuild treats as new). The marks of other rebuilders stay as they are. O(1) amortized.
        /// </summary>
        /// <param name="rebuilderTableId">The rebuilder's table id.</param>
        internal void MarkRebuilt(TableId rebuilderTableId)
        {
            rebuiltAt ??= new DenseIdMap<TableId, TableVersion>();
            rebuiltAt.Insert(rebuilderTableId, new TableVersion(generation, data.Count));
        }

        /// <summary>
        /// Gets whether rows were appended since <see cref="ApplyRebuild"/> (or <see cref="SettleAppends"/>) last ran
        /// against <paramref name="rebuilderTableId"/>: the rows a raw write may have left stale. Always <c>false</c>
        /// for a table without rebuild columns, which no union can make stale. O(1).
        /// </summary>
        /// <param name="rebuilderTableId">The rebuilder's table id.</param>
        /// <returns><c>true</c> if such rows exist.</returns>
        internal bool HasRowsSinceRebuild(TableId rebuilderTableId) => toRebuild.Length > 0 && FirstRowSinceRebuild(rebuilderTableId) < data.Count;

    }

}
