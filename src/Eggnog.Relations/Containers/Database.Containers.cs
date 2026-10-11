using System;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class Database
    {

        /// <summary>
        /// The container environments, created on first use with the database's parallelism.
        /// </summary>
        ContainerValues? containers;

        /// <summary>
        /// Whether <see cref="RebuildContainers"/> is running, during which <see cref="Containers"/> is unavailable, as
        /// egglog takes the containers out of the database for the rebuild (core-relations/src/free_join/mod.rs:358).
        /// </summary>
        bool containersTakenOut;

        /// <summary>
        /// Gets the container environments: egglog's <c>container_values</c> and <c>container_values_mut</c>
        /// (core-relations/src/free_join/mod.rs:349-355). Registering a type needs exclusive access to the database;
        /// interning and lookups are thread-safe (see <see cref="ContainerValues"/>).
        /// </summary>
        /// <exception cref="InvalidOperationException">The containers are being rebuilt (a merge function called back
        /// during <see cref="RebuildContainers"/>).</exception>
        public ContainerValues Containers
        {
            get
            {
                if (containersTakenOut)
                    throw new InvalidOperationException("The database's containers are being rebuilt.");

                return containers ?? CreateContainers();
            }
        }

        /// <summary>
        /// Rebuilds every container against the union-find <paramref name="unionFind"/>, staging the unions that merging
        /// equal containers produces, and flushes them so that the next <see cref="MergeAll"/> applies them. egglog's
        /// <c>rebuild_containers</c> (core-relations/src/free_join/mod.rs:357-363). Requires exclusive access.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The containers are unavailable through <see cref="Containers"/> meanwhile, as egglog takes them out of the
        /// database. The merges run in a fresh execution state carrying <paramref name="timestamp"/> (egglog's
        /// <c>with_execution_state</c>); a failure it reports is recorded in <see cref="Failure"/>.
        /// </para>
        /// <para>
        /// The state's timestamp stamps the unions a <see cref="UnionContainerMerge"/> without a timestamp counter
        /// stages, and the union-find rejects a union older than its newest row (egglog asserts the same,
        /// core-relations/src/uf/mod.rs:499-505). egglog's <c>rebuild_containers(table_id)</c> takes no timestamp because
        /// the bridge's merge reads its timestamp counter, which never goes backwards. So when
        /// <paramref name="timestamp"/> is omitted the state carries the timestamp of the union-find's newest merged
        /// union (0 if it has none), never an older one; a fixed default of 0 would stage a union the next
        /// <see cref="MergeAll"/> rejects as soon as any union carried a later timestamp.
        /// </para>
        /// </remarks>
        /// <param name="unionFind">The union-find table, which provides the rebuilder.</param>
        /// <param name="timestamp">The timestamp the merge state carries (the pass's timestamp in the rebuild loop), or
        /// <c>null</c> for the timestamp of the union-find's newest union.</param>
        /// <returns>What changed; nothing when no container type was ever registered.</returns>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">No such table, or it is taken out.</exception>
        public ContainerRebuildSummary RebuildContainers(TableId unionFind, uint? timestamp = null)
        {
            var table = GetTable(unionFind);
            if (containers is not { } values)
                return new ContainerRebuildSummary();

            containersTakenOut = true;
            try
            {
                var state = new ExecutionState(view, timestamp ?? NewestUnionTimestamp(table));
                var summary = values.RebuildAll(unionFind, table, state);

                // egglog drops the state here, which flushes the unions the merges staged.
                state.Flush();
                RecordFailure(state);
                return summary;
            }
            finally
            {
                containersTakenOut = false;
            }
        }

        /// <summary>
        /// Runs one pass of the bridge's native rebuild loop at <paramref name="nextTimestamp"/>: rebuild the containers
        /// and merge the unions their merges staged into the union-find, then rebuild <paramref name="tables"/> against
        /// the union-find, then re-stamp the rows of <paramref name="tables"/> that mention a container whose meaning
        /// changed in place. The body of egglog-bridge's loop (egglog-bridge/src/lib.rs:666-681). Requires exclusive
        /// access.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Containers go first so that containers that became equal are merged before the table rows mentioning them
        /// collapse; the other order can violate a function declared without a merge (the bridge's comment,
        /// lib.rs:633-665).
        /// </para>
        /// <para>
        /// Defect fixed (egglog-bridge/src/lib.rs:666-668; core-relations/src/free_join/mod.rs:357-388; map §8.1 row 23):
        /// egglog stages the container unions and lets the table rebuild's merge apply them, but the table rebuild
        /// canonicalizes rows against the union-find as it was before those unions. So in the comment's own scenario a
        /// function without a merge mapping <c>x2</c> and <c>x3</c> to <c>vec[x2]</c> and <c>vec[x3]</c> collapses to
        /// two rows for <c>x2</c> with the still-distinct vector ids, and reports a merge conflict. Here the container
        /// unions are merged into the union-find first (only when the containers changed), so the table rebuild sees
        /// them.
        /// </para>
        /// </remarks>
        /// <param name="unionFind">The union-find table.</param>
        /// <param name="tables">The function tables to rebuild and refresh; distinct, and not the union-find.</param>
        /// <param name="nextTimestamp">The pass's timestamp.</param>
        /// <returns>Whether the pass changed anything: a table rebuild, a refresh or a container change.</returns>
        public bool RebuildPass(TableId unionFind, ReadOnlySpan<TableId> tables, uint nextTimestamp)
        {
            var containerRebuild = RebuildContainers(unionFind, nextTimestamp);
            if (containerRebuild.Changed)
                MergeAll(nextTimestamp);

            var tableRebuild = ApplyRebuild(unionFind, tables, nextTimestamp);
            var refreshed = RefreshRowsForValues(tables, containerRebuild.DirtyIds, nextTimestamp);
            return tableRebuild || refreshed || containerRebuild.Changed;
        }

        /// <summary>
        /// Runs the bridge's native rebuild loop to a fixpoint: each pass (<see cref="RebuildPass"/>) runs at the current
        /// value of <paramref name="timestampCounter"/>, which is advanced after it, until a pass changes nothing.
        /// egglog-bridge's <c>rebuild</c> for a union-find with a native rebuilder (egglog-bridge/src/lib.rs:626-682).
        /// Requires exclusive access.
        /// </summary>
        /// <param name="unionFind">The union-find table.</param>
        /// <param name="tables">The function tables; distinct, and not the union-find.</param>
        /// <param name="timestampCounter">The bridge's timestamp counter.</param>
        /// <returns>The number of passes run, at least one.</returns>
        public int Rebuild(TableId unionFind, ReadOnlySpan<TableId> tables, CounterId timestampCounter)
        {
            var passes = 0;
            while (true)
            {
                var next = (uint)ReadCounter(timestampCounter);
                var changed = RebuildPass(unionFind, tables, next);
                IncrementCounter(timestampCounter);
                passes++;
                if (changed == false)
                    return passes;
            }
        }

        /// <summary>
        /// Creates the container environments on first use; racing first uses agree on one instance.
        /// </summary>
        ContainerValues CreateContainers()
        {
            Interlocked.CompareExchange(ref containers, new ContainerValues(parallelism), null);
            return containers!;
        }

        /// <summary>
        /// Gets the timestamp of the newest union <paramref name="table"/> has merged: the default timestamp of
        /// <see cref="RebuildContainers"/>, the smallest one a union staged now can carry without going backwards.
        /// Unions staged but not merged yet are not considered. Constant time.
        /// </summary>
        /// <param name="table">The union-find table.</param>
        /// <returns>The newest union's timestamp; 0 if there is none, or if <paramref name="table"/> is not a
        /// <see cref="DisplacedTable"/> (the only table that provides a rebuilder).</returns>
        static uint NewestUnionTimestamp(Table table)
        {
            if (table is DisplacedTable displaced && displaced.UnionFind.Count is var count and > 0)
                return displaced.UnionFind.GetTimestamp(count - 1);

            return 0;
        }

    }

}
