using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// One shard's share of a <see cref="SortedWritesTable"/> parallel insert: the task-local state of egglog's
    /// <c>parallel_insert</c> closure (core-relations/src/table/mod.rs:830-951) and the tuple
    /// <c>(checker, marked_stale, changed)</c> it reports back, with the sort checker (<c>SortChecker</c>,
    /// mod.rs:1394-1460) reduced to the one sort value the shard's staged rows carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One instance per shard per parallel insert. The task that runs the shard is its only writer while a parallel
    /// loop runs; the coordinating thread reads it once the loop has returned or thrown.
    /// </para>
    /// <para>
    /// The counters are exact at every point, including after a task threw part-way through, so that the coordinator
    /// can keep the table's stale count exact on every path.
    /// </para>
    /// </remarks>
    internal sealed class ParallelInsertShard
    {

        /// <summary>
        /// The shard this state belongs to.
        /// </summary>
        public readonly int ShardIndex;

        /// <summary>
        /// The shard's folded batches, in staging order: each holds at most
        /// <see cref="SortedWritesTable.ParallelInsertBatchSize"/> live rows. Owned by this state until written to the
        /// table or released by <see cref="DisposeBatches"/>.
        /// </summary>
        public readonly List<StagedOutputs> Batches;

        /// <summary>
        /// The task's clone of the caller's execution state, which the merge function runs in; <c>null</c> while the
        /// shard had nothing to insert.
        /// </summary>
        public ExecutionState? Local;

        /// <summary>
        /// Whether the shard appended a new key or a merge changed a row.
        /// </summary>
        public bool Changed;

        /// <summary>
        /// Whether the shard's merge functions staged writes through <see cref="Local"/>.
        /// </summary>
        public bool StateChanged;

        /// <summary>
        /// The number of rows of the table the shard has tombstoned so far.
        /// </summary>
        public int MarkedStale;

        /// <summary>
        /// Whether <see cref="SortValue"/> holds the sort value of the shard's live staged rows.
        /// </summary>
        public bool HasSortValue;

        /// <summary>
        /// The sort value the shard's live staged rows carry, when <see cref="HasSortValue"/>.
        /// </summary>
        public uint SortValue;

        /// <summary>
        /// Whether a live staged row broke the sort order: its sort value was below the table's last, or differed from
        /// <see cref="SortValue"/>. The insert then throws before writing anything.
        /// </summary>
        public bool SortViolation;

        /// <summary>
        /// The offending sort value, when <see cref="SortViolation"/>.
        /// </summary>
        public uint ViolatingValue;

        /// <summary>
        /// Initializes the state of shard <paramref name="shardIndex"/>, with no staged rows.
        /// </summary>
        /// <param name="shardIndex">The shard.</param>
        public ParallelInsertShard(int shardIndex)
        {
            ShardIndex = shardIndex;
            Batches = new List<StagedOutputs>(1);
        }

        /// <summary>
        /// Releases every batch still held and forgets it. Safe to call more than once.
        /// </summary>
        public void DisposeBatches()
        {
            for (var i = 0; i < Batches.Count; i++)
                Batches[i].Dispose();

            Batches.Clear();
        }

    }

}
