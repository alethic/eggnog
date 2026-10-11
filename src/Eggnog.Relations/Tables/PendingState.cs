using System.Collections.Concurrent;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The writes staged for a table and not yet merged: egglog's <c>PendingState</c>
    /// (core-relations/src/table/mod.rs:1288-1365). One queue of row buffers and one queue of removal-key buffers per
    /// shard, plus running totals that size the next merge and choose between its serial and parallel paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="MutationBuffer"/>s pre-shard what they stage with <see cref="ShardData"/> and publish whole buffers,
    /// one queue operation per shard per flush, never one per row. The queues are lock-free and take any number of
    /// concurrent producers (crossbeam's <c>SegQueue</c> becomes <see cref="ConcurrentQueue{T}"/>); the table drains
    /// them during its exclusive merge.
    /// </para>
    /// <para>
    /// The totals are hints: they are added to after a buffer is enqueued and swapped to zero by the merge, so a flush
    /// racing a merge can be counted in the next one. The merge only uses them to reserve space and pick a path.
    /// </para>
    /// <para>
    /// Buffers in the queues are owned by the state: whoever dequeues one disposes it, returning its storage to the
    /// array pool.
    /// </para>
    /// </remarks>
    internal sealed class PendingState
    {

        readonly ShardData shardData;
        readonly ConcurrentQueue<RowBuffer>[] rows;
        readonly ConcurrentQueue<ArbitraryRowBuffer>[] removals;
        long totalRows;
        long totalRemovals;

        /// <summary>
        /// Initializes an empty state with one queue pair per shard of <paramref name="shardData"/>.
        /// </summary>
        /// <param name="shardData">The table's shard layout.</param>
        public PendingState(ShardData shardData)
        {
            this.shardData = shardData;
            rows = new ConcurrentQueue<RowBuffer>[shardData.Count];
            removals = new ConcurrentQueue<ArbitraryRowBuffer>[shardData.Count];
            for (var i = 0; i < rows.Length; i++)
            {
                rows[i] = new ConcurrentQueue<RowBuffer>();
                removals[i] = new ConcurrentQueue<ArbitraryRowBuffer>();
            }
        }

        /// <summary>
        /// Gets the shard layout that staged rows are sharded by.
        /// </summary>
        public ShardData ShardData => shardData;

        /// <summary>
        /// Gets the number of shards.
        /// </summary>
        public int ShardCount => rows.Length;

        /// <summary>
        /// Gets the number of rows flushed since the last <see cref="TakeTotalRows"/>.
        /// </summary>
        public long TotalRows => Interlocked.Read(ref totalRows);

        /// <summary>
        /// Gets the number of removal keys flushed since the last <see cref="TakeTotalRemovals"/>.
        /// </summary>
        public long TotalRemovals => Interlocked.Read(ref totalRemovals);

        /// <summary>
        /// Gets the queue of row buffers staged for shard <paramref name="shard"/>.
        /// </summary>
        /// <param name="shard">The shard index.</param>
        /// <returns>The queue.</returns>
        public ConcurrentQueue<RowBuffer> RowQueue(int shard) => rows[shard];

        /// <summary>
        /// Gets the queue of removal-key buffers staged for shard <paramref name="shard"/>.
        /// </summary>
        /// <param name="shard">The shard index.</param>
        /// <returns>The queue.</returns>
        public ConcurrentQueue<ArbitraryRowBuffer> RemovalQueue(int shard) => removals[shard];

        /// <summary>
        /// Publishes a buffer of rows staged for <paramref name="shard"/>, which the state now owns. Thread-safe.
        /// </summary>
        /// <param name="shard">The shard index.</param>
        /// <param name="buffer">The rows.</param>
        public void EnqueueRows(int shard, RowBuffer buffer)
        {
            var count = buffer.Count;
            rows[shard].Enqueue(buffer);
            Interlocked.Add(ref totalRows, count);
        }

        /// <summary>
        /// Publishes a buffer of removal keys staged for <paramref name="shard"/>, which the state now owns. Thread-safe.
        /// </summary>
        /// <param name="shard">The shard index.</param>
        /// <param name="buffer">The keys.</param>
        public void EnqueueRemovals(int shard, ArbitraryRowBuffer buffer)
        {
            var count = buffer.Count;
            removals[shard].Enqueue(buffer);
            Interlocked.Add(ref totalRemovals, count);
        }

        /// <summary>
        /// Returns the row total and resets it to zero, as the merge's <c>total_rows.swap(0)</c> does.
        /// </summary>
        /// <returns>The rows flushed since the last call.</returns>
        public long TakeTotalRows() => Interlocked.Exchange(ref totalRows, 0);

        /// <summary>
        /// Returns the removal total and resets it to zero.
        /// </summary>
        /// <returns>The removal keys flushed since the last call.</returns>
        public long TakeTotalRemovals() => Interlocked.Exchange(ref totalRemovals, 0);

        /// <summary>
        /// Discards every staged buffer, returning their storage to the pool, and resets the totals. egglog's
        /// <c>PendingState::clear</c>, which leaves the totals alone; resetting them keeps the next merge from sizing
        /// itself for rows that no longer exist. Must not race a flush.
        /// </summary>
        public void Clear()
        {
            for (var i = 0; i < rows.Length; i++)
            {
                while (rows[i].TryDequeue(out var buffer))
                    buffer.Dispose();

                while (removals[i].TryDequeue(out var keys))
                    keys.Dispose();
            }

            Interlocked.Exchange(ref totalRows, 0);
            Interlocked.Exchange(ref totalRemovals, 0);
        }

        /// <summary>
        /// Creates a state with the same layout holding copies of every staged buffer, in queue order, and the same
        /// totals; this state keeps its own buffers. egglog's <c>deep_copy</c>, used when a table is cloned (the
        /// clone gets the copy; mutation buffers created earlier keep flushing into this state). Must not race a
        /// flush or a merge. O(staged cells).
        /// </summary>
        /// <returns>The copy.</returns>
        public PendingState DeepCopy()
        {
            var copy = new PendingState(shardData);
            for (var i = 0; i < rows.Length; i++)
            {
                foreach (var buffer in rows[i].ToArray())
                    copy.rows[i].Enqueue(buffer.Clone());

                foreach (var keys in removals[i].ToArray())
                    copy.removals[i].Enqueue(keys.Clone());
            }

            copy.totalRows = TotalRows;
            copy.totalRemovals = TotalRemovals;
            return copy;
        }

    }

}
