using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Remembers, per providing table, the version seen at the last rebuild, so that the next rebuild scans only the
    /// provider's rows appended since: egglog's <c>SubsetTracker</c> (core-relations/src/common.rs:194-222). A table uses
    /// it to find the union-find rows (displaced ids) it has not yet canonicalized against.
    /// </summary>
    /// <remarks>
    /// Not thread-safe; owned by one table and used during its exclusive rebuild.
    /// </remarks>
    internal sealed class SubsetTracker
    {

        readonly DenseIdMap<TableId, TableVersion> lastRebuiltAt;

        /// <summary>
        /// Initializes a tracker that has seen nothing.
        /// </summary>
        public SubsetTracker()
        {
            lastRebuiltAt = new DenseIdMap<TableId, TableVersion>();
        }

        SubsetTracker(DenseIdMap<TableId, TableVersion> lastRebuiltAt)
        {
            this.lastRebuiltAt = lastRebuiltAt;
        }

        /// <summary>
        /// Returns the rows of <paramref name="table"/> to scan to see every update since the last call for
        /// <paramref name="tableId"/>, and records the table's current version. The first call, and any call after the
        /// table's major version changed, returns the whole table, so results may overlap earlier ones. egglog's
        /// <c>recent_updates</c>. O(1).
        /// </summary>
        /// <param name="tableId">The providing table's id.</param>
        /// <param name="table">The providing table.</param>
        /// <returns>A dense subset of the table.</returns>
        public Subset RecentUpdates(TableId tableId, Table table)
        {
            ArgumentNullException.ThrowIfNull(table);

            var current = table.Version;
            var result = lastRebuiltAt.TryGetValue(tableId, out var last) && last.Major == current.Major
                ? table.UpdatesSince(last.Minor)
                : table.All();
            lastRebuiltAt.Insert(tableId, current);
            return result;
        }

        /// <summary>
        /// Forgets every recorded version, so that the next call for each table returns the whole table.
        /// </summary>
        public void Reset()
        {
            lastRebuiltAt.Clear();
        }

        /// <summary>
        /// Creates an independent copy.
        /// </summary>
        /// <returns>The copy.</returns>
        public SubsetTracker Clone()
        {
            return new SubsetTracker(lastRebuiltAt.Clone());
        }

    }

}
