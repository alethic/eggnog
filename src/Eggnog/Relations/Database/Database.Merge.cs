using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    internal sealed partial class Database
    {

        /// <summary>
        /// The tables taken from the notification list by the current merge round. Merge scratch; merging is exclusive.
        /// </summary>
        readonly List<TableId> notified = new();

        /// <summary>
        /// The current round's tables as sort keys <c>(stratum &lt;&lt; 32) | id</c>, sorted. Merge scratch.
        /// </summary>
        long[] roundOrder = Array.Empty<long>();

        /// <summary>
        /// The tables of the stratum being merged, taken out of the table map. Merge scratch.
        /// </summary>
        TableInfo?[] stratumInfos = Array.Empty<TableInfo?>();

        /// <summary>
        /// Merges every staged write into its table, repeatedly, until no table is notified, and returns whether
        /// anything changed. egglog's <c>merge_all</c> (core-relations/src/free_join/mod.rs:501-588). Requires
        /// exclusive access.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each round takes the notified tables from the notification list and merges them stratum by stratum, in
        /// stratum order and, within a stratum, in ascending table id (the order of egglog's strata path, mod.rs:533-572).
        /// The tables of a stratum are taken out of the table map together, so no merge function can read a table of
        /// its own stratum that is being merged; above <see cref="ParallelHeuristics.DbLevelThreshold"/> rows they
        /// merge in parallel. Each merge runs in a fresh execution state over <see cref="ReadOnlyView"/>, flushed after
        /// the merge, so writes its merge function stages (unions, say) reach their tables before the next stratum
        /// merges; tables they notify that were not in the round are merged in the next round. A round changed
        /// something when a merge added rows or its merge function staged writes (egglog's
        /// <c>added || es.changed</c>; removals alone do not count).
        /// </para>
        /// <para>
        /// egglog merges rounds of fewer than four tables with <c>merge_simple</c>, in notification order and ignoring
        /// strata (core-relations/src/free_join/mod.rs:514-517, 593-605), so there a merge function can read a read
        /// dependency whose staged writes (a rebuild's rewrite) are not merged yet. Here every round respects strata; a
        /// small round only skips the parallel machinery.
        /// </para>
        /// <para>
        /// egglog pre-allocates buffers for each merging table's declared write dependencies, because it removes the
        /// merging tables from the view it stages through. Here staging goes through <see cref="Catalog"/>, which keeps
        /// every table, so buffers are created on first use; writes into a table of the same stratum stay buffered until
        /// the writing state flushes, as egglog's pre-allocated buffers do.
        /// </para>
        /// <para>
        /// Every merge state carries <paramref name="timestamp"/> as its <see cref="ExecutionState.Timestamp"/>: the
        /// epoch captured for the run or rebuild pass whose writes are being merged (every write of a run or pass,
        /// merge functions' included, is stamped with the captured epoch). Rows a merge function writes without an
        /// incoming row to copy a timestamp from (a <see cref="UnionIdMerge"/> without a timestamp column, a bridge
        /// <c>Function</c> merge minting a term) carry it; egglog's merge functions read the bridge's timestamp counter
        /// instead, which holds the same value. If a merge throws, the exception propagates (unwrapped, also from the
        /// parallel arm) after every table is back in the table map, the interrupted round's tables are notified again,
        /// and the caches are reset.
        /// </para>
        /// <para>
        /// Afterwards every cached index is re-armed, so its next use refreshes it, and
        /// <see cref="TotalSizeEstimate"/> is recomputed (mod.rs:575-586).
        /// </para>
        /// </remarks>
        /// <param name="timestamp">The epoch captured for the run or rebuild pass being merged, which every merge state
        /// carries; <c>null</c> for the store clock's current value, or zero for a database without a store (writes made
        /// outside any run, loading data).</param>
        /// <returns><c>true</c> if any merge added rows or staged writes.</returns>
        /// <exception cref="InvalidOperationException">The database is frozen, or it is a faulted child context's: an
        /// operation of the child failed, and nothing it staged may merge; a faulted child can only be
        /// disposed.</exception>
        public bool MergeAll(uint? timestamp = null)
        {
            ThrowIfFrozen();
            return MergeAllCore(ResolveTimestamp(timestamp));
        }

        /// <summary>
        /// <see cref="MergeAll"/> with the timestamp resolved.
        /// </summary>
        /// <remarks>
        /// In a child context's database: strata always merge serially (a layer's merge creates layers and hides rows
        /// on the domain thread); every round starts by adopting the layers created during a search
        /// (<see cref="TakeRound"/>); the loop stops before a round once a contradiction stopped the context, leaving
        /// the remaining writes pending; and the epilogue seals every hidden set the merge touched and drops the views
        /// whose structure changed (<see cref="ContextTables.AfterMerge"/>). At the root each costs one null test.
        /// </remarks>
        bool MergeAllCore(uint timestamp)
        {
            var everChanged = false;
            bool parallel;
            if (layers is { } l)
            {
                // A faulted child never merges again, so nothing a failed operation staged can land. Tested inside the
                // root's existing null test: zero root cost.
                l.Owner.ThrowIfFaulted();
                parallel = false;
            }
            else
            {
                parallel = parallelism.ParallelizeDbLevelOp(totalSizeEstimate);
            }

            var count = 0;
            try
            {
                while (layers?.Owner.IsContradicted != true && (count = TakeRound()) > 0)
                {
                    everChanged |= MergeRound(count, parallel, timestamp);
                    count = 0;
                }
            }
            catch
            {
                // Keep the interrupted round's tables notified, so that the writes of the tables not merged yet are not
                // forgotten; merging a merged table again is a no-op.
                for (var i = 0; i < count; i++)
                    catalog.Notifications.Notify(TableOf(roundOrder[i]));

                throw;
            }
            finally
            {
                ResetIndexesAndEstimateSize();
                layers?.AfterMerge();
            }

            return everChanged;
        }

        /// <summary>
        /// Merges the staged writes of <paramref name="table"/> alone and returns whether rows were added. egglog's
        /// <c>merge_table</c> (mod.rs:607-623). Writes its merge function stages notify their tables for the next
        /// <see cref="MergeAll"/>, which callers should prefer. Requires exclusive access.
        /// </summary>
        /// <remarks>
        /// <para>
        /// egglog leaves the table's cached indexes armed, so until the next <see cref="MergeAll"/> they miss the rows
        /// this merge added; here the table's indexes are reset like <see cref="MergeAll"/> and
        /// <see cref="ClearTable"/> reset them.
        /// </para>
        /// <para>
        /// If the merge throws, the exception propagates after the table is back in the table map, its indexes are
        /// reset and <see cref="TotalSizeEstimate"/> accounts for the rows the merge did add, as after a
        /// <see cref="MergeAll"/> that throws.
        /// </para>
        /// </remarks>
        /// <param name="table">The table id.</param>
        /// <param name="timestamp">The epoch captured for the run or pass being merged, which the merge state carries
        /// (see <see cref="MergeAll"/>); <c>null</c> for the store clock's current value, or zero without a store.</param>
        /// <returns><c>true</c> if the merge added rows.</returns>
        /// <exception cref="InvalidOperationException">The database is frozen, or it is a faulted child context's
        /// (see <see cref="MergeAll"/>).</exception>
        public bool MergeTable(TableId table, uint? timestamp = null)
        {
            ThrowIfFrozen();
            var resolved = ResolveTimestamp(timestamp);
            if (layers is { } l)
            {
                // A faulted child never merges again, so nothing a failed operation staged can land.
                l.Owner.ThrowIfFaulted();
                l.AdoptDeferred();
            }

            var info = TakeOut(table);
            var before = info.Table.Count;
            try
            {
                var state = new ExecutionState(view, resolved);
                var change = info.Table.Merge(state);
                state.Flush();
                RecordFailure(state);
                if ((change.Added || change.Removed) && IsContextPrivate(table) == false)
                    BumpChangeStamp();

                return change.Added;
            }
            finally
            {
                totalSizeEstimate += info.Table.Count - before;
                info.ResetIndexes();
                PutBack(table, info);
                layers?.AfterMerge();
            }
        }

        /// <summary>
        /// Takes the notified tables for one round and sorts them by stratum, then id, into <see cref="roundOrder"/>.
        /// </summary>
        /// <returns>The number of tables.</returns>
        int TakeRound()
        {
            // Layers created during a search enter the table map before the round takes its tables.
            layers?.AdoptDeferred();
            notified.Clear();
            var count = catalog.Notifications.Reset(notified);
            if (count == 0)
                return 0;

            if (roundOrder.Length < count)
                roundOrder = new long[Math.Max(count, roundOrder.Length * 2)];

            for (var i = 0; i < count; i++)
            {
                var table = notified[i];
                roundOrder[i] = ((long)deps.LevelOf(table) << 32) | (uint)table.Rep;
            }

            Array.Sort(roundOrder, 0, count);
            return count;
        }

        /// <summary>
        /// Merges the round in <see cref="roundOrder"/>, one stratum at a time.
        /// </summary>
        /// <returns>Whether any merge added rows or staged writes.</returns>
        bool MergeRound(int count, bool parallel, uint timestamp)
        {
            var changed = false;
            var start = 0;
            while (start < count)
            {
                var level = LevelOf(roundOrder[start]);
                var end = start + 1;
                while (end < count && LevelOf(roundOrder[end]) == level)
                    end++;

                changed |= MergeStratum(start, end - start, parallel, timestamp);
                start = end;
            }

            return changed;
        }

        /// <summary>
        /// Merges the tables <see cref="roundOrder"/>[<paramref name="start"/>..+<paramref name="length"/>], all of one
        /// stratum: takes them all out of the table map (egglog: mod.rs:544-548), merges them, and puts them back.
        /// </summary>
        /// <returns>Whether any merge added rows or staged writes.</returns>
        bool MergeStratum(int start, int length, bool parallel, uint timestamp)
        {
            if (stratumInfos.Length < length)
                stratumInfos = new TableInfo?[Math.Max(length, stratumInfos.Length * 2)];

            var infos = stratumInfos;
            try
            {
                for (var i = 0; i < length; i++)
                    infos[i] = TakeOut(TableOf(roundOrder[start + i]));

                if (parallel && length > 1)
                    return MergeParallel(infos, start, length, timestamp);

                var changed = false;
                for (var i = 0; i < length; i++)
                    changed |= MergeOne(infos[i]!, TableOf(roundOrder[start + i]), timestamp);

                return changed;
            }
            finally
            {
                for (var i = 0; i < length; i++)
                {
                    if (infos[i] is { } info)
                    {
                        PutBack(TableOf(roundOrder[start + i]), info);
                        infos[i] = null;
                    }
                }
            }
        }

        /// <summary>
        /// Merges the tables of one stratum in parallel, one task per table: egglog's <c>par_iter_mut</c> arm
        /// (mod.rs:550-558). An exception one merge throws propagates as itself rather than wrapped in the
        /// <see cref="AggregateException"/> of <see cref="Parallel"/>, so callers see the same exception as on the
        /// serial path.
        /// </summary>
        /// <returns>Whether any merge added rows or staged writes.</returns>
        bool MergeParallel(TableInfo?[] infos, int start, int length, uint timestamp)
        {
            var changed = 0;
            try
            {
                Parallel.For(0, length, parallelism.Options, i =>
                {
                    if (MergeOne(infos[i]!, TableOf(roundOrder[start + i]), timestamp))
                        Interlocked.Exchange(ref changed, 1);
                });
            }
            catch (AggregateException e)
            {
                RethrowSingle(e);
                throw;
            }

            return changed != 0;
        }

        /// <summary>
        /// Merges one table in a fresh execution state carrying <paramref name="timestamp"/> and flushes the state: the
        /// body of egglog's merge closures (mod.rs:554-555, 563-564, 598-599). Thread-safe across distinct tables.
        /// A merge that added or removed rows of a table that is not context-private moves <see cref="ChangeStamp"/>: one
        /// test per table merge.
        /// </summary>
        /// <returns>Whether the merge added rows or its merge function staged writes.</returns>
        bool MergeOne(TableInfo info, TableId table, uint timestamp)
        {
            var state = new ExecutionState(view, timestamp);
            var change = info.Table.Merge(state);
            state.Flush();
            RecordFailure(state);
            if ((change.Added || change.Removed) && IsContextPrivate(table) == false)
                BumpChangeStamp();

            return change.Added || state.Changed;
        }

        /// <summary>
        /// Re-arms every cached index and recomputes <see cref="TotalSizeEstimate"/>: the tail of egglog's
        /// <c>merge_all</c> (mod.rs:575-586).
        /// </summary>
        void ResetIndexesAndEstimateSize()
        {
            long size = 0;
            foreach (var (_, info) in tables)
            {
                info.ResetIndexes();
                size += info.Table.Count;
            }

            totalSizeEstimate = size;
        }

        /// <summary>
        /// Rethrows the inner exception of <paramref name="exception"/>, with its original stack trace, when it holds
        /// exactly one (a parallel loop over tables in which one iteration threw); returns otherwise, for the caller to
        /// rethrow the aggregate.
        /// </summary>
        /// <param name="exception">The exception a parallel loop threw.</param>
        static void RethrowSingle(AggregateException exception)
        {
            var inner = exception.Flatten().InnerExceptions;
            if (inner.Count == 1)
                ExceptionDispatchInfo.Capture(inner[0]).Throw();
        }

        /// <summary>
        /// Gets the stratum of a round sort key.
        /// </summary>
        static int LevelOf(long key) => (int)(key >> 32);

        /// <summary>
        /// Gets the table of a round sort key.
        /// </summary>
        static TableId TableOf(long key) => new((int)(uint)key);

    }

}
