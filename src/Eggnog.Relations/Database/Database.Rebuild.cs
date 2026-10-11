using System;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    internal sealed partial class Database
    {

        /// <summary>
        /// Rebuilds every table of <paramref name="tablesToRebuild"/> against the rebuilder that
        /// <paramref name="rebuilder"/> (the union-find) provides, then merges. egglog's <c>apply_rebuild</c>
        /// (core-relations/src/free_join/mod.rs:365-388). Requires exclusive access.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The rebuilder table is taken out of the table map while the tables stage their rewrites
        /// (<see cref="Table.ApplyRebuild"/>: remove each changed row's key, insert the canonical row stamped
        /// <paramref name="nextTimestamp"/>), each in a fresh execution state. Above
        /// <see cref="ParallelHeuristics.DbLevelThreshold"/> rows in total the tables rebuild in parallel (egglog's
        /// <c>run_on_tables</c>, mod.rs:412-444). Tables that staged anything are notified, the rebuilder is put back,
        /// and <see cref="MergeAll"/> applies the rewrites: colliding keys go through their merge functions, which is
        /// congruence closure, and the unions those stage merge into the rebuilder in the same fixpoint. That merge runs
        /// at <paramref name="nextTimestamp"/> too, so the rows and unions merge functions write carry the pass's epoch
        /// (map §4.2 C1; egglog's merge functions read the bridge's timestamp counter, which holds that value).
        /// </para>
        /// <para>
        /// The parallel arm notifies tables in completion order, which egglog's small-batch merge then followed; here
        /// <see cref="MergeAll"/> sorts every round by stratum, so the order of notification does not matter (map §8.1
        /// row 11).
        /// </para>
        /// </remarks>
        /// <param name="rebuilder">The table providing the rebuilder.</param>
        /// <param name="tablesToRebuild">The tables to rebuild; distinct, and not <paramref name="rebuilder"/>.</param>
        /// <param name="nextTimestamp">The timestamp of the pass: rewritten rows get it in their sort column, and the
        /// merge states carry it.</param>
        /// <returns>The result of the <see cref="MergeAll"/>: whether anything changed.</returns>
        /// <exception cref="ArgumentException">A table is listed twice, or the rebuilder is listed.</exception>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">A table does not exist.</exception>
        public bool ApplyRebuild(TableId rebuilder, ReadOnlySpan<TableId> tablesToRebuild, uint nextTimestamp)
        {
            var operation = new RebuildTableOperation(rebuilder, GetTable(rebuilder), nextTimestamp);
            RunWithTakenOut(rebuilder, tablesToRebuild, operation);
            return MergeAll(nextTimestamp);
        }

        /// <summary>
        /// Re-stamps with <paramref name="nextTimestamp"/> every row of <paramref name="tablesToRefresh"/> whose id
        /// columns mention one of <paramref name="dirtyIds"/>, then merges. egglog's <c>refresh_rows_for_values</c>
        /// (mod.rs:390-410): the follow-up to a container rebuild, for ids whose meaning changed without their identity
        /// changing, so that seminaive evaluation sees the parent rows as new. Runs after the ordinary table rebuild.
        /// Requires exclusive access.
        /// </summary>
        /// <param name="tablesToRefresh">The tables to refresh; distinct.</param>
        /// <param name="dirtyIds">The ids whose meaning changed.</param>
        /// <param name="nextTimestamp">The timestamp of the pass: refreshed rows get it, and the merge states carry it.</param>
        /// <returns><c>false</c> without dirty ids; otherwise the result of the <see cref="MergeAll"/>.</returns>
        /// <exception cref="ArgumentException">A table is listed twice.</exception>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">A table does not exist.</exception>
        public bool RefreshRowsForValues(ReadOnlySpan<TableId> tablesToRefresh, ReadOnlySpan<uint> dirtyIds, uint nextTimestamp)
        {
            if (dirtyIds.IsEmpty)
                return false;

            CheckTableList(tablesToRefresh, null, nameof(tablesToRefresh));
            var operation = new RefreshTableOperation(dirtyIds.ToArray(), nextTimestamp);
            RunOnTablesCore(tablesToRefresh, operation);
            return MergeAll(nextTimestamp);
        }

        /// <summary>
        /// Runs <paramref name="operation"/> on the tables of <paramref name="ids"/> (<see cref="RunOnTables{TOperation}"/>) while
        /// <paramref name="excluded"/> is taken out of the table map: the frame of egglog's <c>apply_rebuild</c>
        /// (mod.rs:377-386), which takes the rebuilder table out so that the per-table states cannot see it. Validates
        /// the list first.
        /// </summary>
        /// <typeparam name="TOperation">The operation.</typeparam>
        /// <param name="excluded">The table to take out, which <paramref name="ids"/> must not list.</param>
        /// <param name="ids">The tables to run on; distinct.</param>
        /// <param name="operation">The operation.</param>
        internal void RunWithTakenOut<TOperation>(TableId excluded, ReadOnlySpan<TableId> ids, TOperation operation)
            where TOperation : struct, ITableOperation
        {
            GetTableInfo(excluded);
            CheckTableList(ids, excluded, nameof(ids));

            var info = TakeOut(excluded);
            try
            {
                RunOnTablesCore(ids, operation);
            }
            finally
            {
                PutBack(excluded, info);
            }
        }

        /// <summary>
        /// Runs <paramref name="operation"/> on each table of <paramref name="ids"/> with the table taken out of the
        /// table map, and notifies the tables it reports changed: egglog's <c>run_on_tables</c> (mod.rs:412-444).
        /// Serially, one table out at a time, or above <see cref="ParallelHeuristics.DbLevelThreshold"/> rows in total
        /// in parallel, with every listed table out at once. Validates the list first. Requires exclusive access.
        /// </summary>
        /// <typeparam name="TOperation">The operation; a struct, so the loop is specialized for it.</typeparam>
        /// <param name="ids">The tables; existing and distinct.</param>
        /// <param name="operation">The operation.</param>
        internal void RunOnTables<TOperation>(ReadOnlySpan<TableId> ids, TOperation operation)
            where TOperation : struct, ITableOperation
        {
            CheckTableList(ids, null, nameof(ids));
            RunOnTablesCore(ids, operation);
        }

        /// <summary>
        /// <see cref="RunOnTables{TOperation}"/> on a validated list.
        /// </summary>
        void RunOnTablesCore<TOperation>(ReadOnlySpan<TableId> ids, TOperation operation)
            where TOperation : struct, ITableOperation
        {
            if (ids.Length > 1 && parallelism.ParallelizeDbLevelOp(totalSizeEstimate))
            {
                RunOnTablesParallel(ids.ToArray(), operation);
                return;
            }

            foreach (var id in ids)
            {
                var info = TakeOut(id);
                bool changed;
                try
                {
                    changed = operation.Run(id, info, view);
                }
                finally
                {
                    PutBack(id, info);
                }

                if (changed)
                    catalog.Notifications.Notify(id);
            }
        }

        /// <summary>
        /// The parallel arm of <see cref="RunOnTablesCore{TOperation}"/>. An exception one operation throws propagates
        /// as itself rather than wrapped in an <see cref="AggregateException"/>, as on the serial arm.
        /// </summary>
        void RunOnTablesParallel<TOperation>(TableId[] ids, TOperation operation)
            where TOperation : struct, ITableOperation
        {
            var infos = new TableInfo?[ids.Length];
            try
            {
                for (var i = 0; i < ids.Length; i++)
                    infos[i] = TakeOut(ids[i]);

                Parallel.For(0, ids.Length, parallelism.Options, i =>
                {
                    if (operation.Run(ids[i], infos[i]!, view))
                        catalog.Notifications.Notify(ids[i]);
                });
            }
            catch (AggregateException e)
            {
                RethrowSingle(e);
                throw;
            }
            finally
            {
                for (var i = 0; i < ids.Length; i++)
                    if (infos[i] is { } info)
                        PutBack(ids[i], info);
            }
        }

        /// <summary>
        /// Checks that every table of <paramref name="ids"/> exists, none repeats, and none is
        /// <paramref name="excluded"/>, so that taking them out cannot fail half-way (egglog's <c>take(..).unwrap()</c>
        /// panics there).
        /// </summary>
        void CheckTableList(ReadOnlySpan<TableId> ids, TableId? excluded, string parameterName)
        {
            var seen = new FixedBitSet(tables.IdCount);
            foreach (var id in ids)
            {
                GetTableInfo(id);
                if (id == excluded)
                    throw new ArgumentException($"Table {id} provides the rebuild and cannot be rebuilt by it.", parameterName);

                if (seen.Insert(id.Index) == false)
                    throw new ArgumentException($"Table {id} is listed twice.", parameterName);
            }
        }

    }

}
