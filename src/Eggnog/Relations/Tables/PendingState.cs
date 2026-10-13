using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
    /// <para>
    /// Sealing: once the table's context freezes, <see cref="Seal"/> makes every later publication throw, so nothing
    /// can be staged into a frozen table. The check is one volatile read per published buffer.
    /// </para>
    /// <para>
    /// Lazy queues: a child's tables create their queues on first publication, behind the same flag read as the seal
    /// (<see cref="PendingState(ShardData, bool)"/>), so lazy queues add nothing to a root or bare table's publication.
    /// </para>
    /// </remarks>
    internal sealed class PendingState
    {

        /// <summary>
        /// The bit of <see cref="flags"/> set once the state is sealed.
        /// </summary>
        const int SealedFlag = 1;

        /// <summary>
        /// The bit of <see cref="flags"/> set while some queue of a lazy state is still the shared empty queue.
        /// </summary>
        const int LazyFlag = 2;

        /// <summary>
        /// The queue a lazy state's row slot holds until its first publication: never enqueued into, so it stays empty
        /// and every reader (<c>IsEmpty</c>, <c>TryDequeue</c>, enumeration) sees nothing.
        /// </summary>
        static readonly ConcurrentQueue<RowBuffer> NoRows = new();

        /// <summary>
        /// The removal-queue counterpart of <see cref="NoRows"/>.
        /// </summary>
        static readonly ConcurrentQueue<ArbitraryRowBuffer> NoRemovals = new();

        readonly ShardData shardData;
        readonly ConcurrentQueue<RowBuffer>[] rows;
        readonly ConcurrentQueue<ArbitraryRowBuffer>[] removals;
        long totalRows;
        long totalRemovals;

        /// <summary>
        /// <see cref="SealedFlag"/> and <see cref="LazyFlag"/>. Zero for an eager state that is not sealed, so a
        /// publication pays one volatile read and one compare for both flags together.
        /// </summary>
        volatile int flags;

        /// <summary>
        /// The queues of a lazy state not created yet; zero for an eager one.
        /// </summary>
        int missingQueues;

        /// <summary>
        /// Initializes an empty state with one queue pair per shard of <paramref name="shardData"/>, created now: the
        /// state of every root and bare table.
        /// </summary>
        /// <param name="shardData">The table's shard layout.</param>
        public PendingState(ShardData shardData)
            : this(shardData, lazyQueues: false)
        {

        }

        /// <summary>
        /// Initializes an empty state with one queue pair per shard of <paramref name="shardData"/>, created now or,
        /// with <paramref name="lazyQueues"/>, on each queue's first publication: a child's layer and union-find table
        /// pay nothing for queues they never stage into (each <see cref="ConcurrentQueue{T}"/> allocates a 32-slot
        /// segment). Readers see a shared empty queue until then.
        /// </summary>
        /// <param name="shardData">The table's shard layout.</param>
        /// <param name="lazyQueues">Whether to create the queues on first use.</param>
        public PendingState(ShardData shardData, bool lazyQueues)
        {
            this.shardData = shardData;
            rows = new ConcurrentQueue<RowBuffer>[shardData.Count];
            removals = new ConcurrentQueue<ArbitraryRowBuffer>[shardData.Count];
            if (lazyQueues)
            {
                Array.Fill(rows, NoRows);
                Array.Fill(removals, NoRemovals);
                missingQueues = 2 * rows.Length;
                flags = LazyFlag;
                return;
            }

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
        /// Gets whether the state is sealed: its table belongs to a frozen context and accepts no staged writes.
        /// </summary>
        public bool IsSealed => (flags & SealedFlag) != 0;

        /// <summary>
        /// Seals the state: every later <see cref="EnqueueRows"/> and <see cref="EnqueueRemovals"/> throws. Called when
        /// the table's context freezes, after its pending writes were merged. Irreversible.
        /// </summary>
        public void Seal() => Interlocked.Or(ref flags, SealedFlag);

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
        /// <exception cref="InvalidOperationException">The state is sealed: its context is frozen. The buffer is
        /// disposed.</exception>
        public void EnqueueRows(int shard, RowBuffer buffer)
        {
            // One volatile read and one compare: an eager state that is not sealed has no flag set.
            if (flags != 0)
                PrepareRows(shard, buffer);

            var count = buffer.Count;
            rows[shard].Enqueue(buffer);
            Interlocked.Add(ref totalRows, count);
        }

        /// <summary>
        /// Publishes a buffer of removal keys staged for <paramref name="shard"/>, which the state now owns. Thread-safe.
        /// </summary>
        /// <param name="shard">The shard index.</param>
        /// <param name="buffer">The keys.</param>
        /// <exception cref="InvalidOperationException">The state is sealed: its context is frozen. The buffer is
        /// disposed.</exception>
        public void EnqueueRemovals(int shard, ArbitraryRowBuffer buffer)
        {
            if (flags != 0)
                PrepareRemovals(shard, buffer);

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
        /// Gets whether nothing is staged: every queue is empty. O(shards). Must not race a flush.
        /// </summary>
        public bool IsEmpty
        {
            get
            {
                for (var i = 0; i < rows.Length; i++)
                    if (rows[i].IsEmpty == false || removals[i].IsEmpty == false)
                        return false;

                return true;
            }
        }

        /// <summary>
        /// Records how many buffers each queue holds, so that <see cref="DiscardSince"/> can later drop everything
        /// staged after this point. Not in egglog, whose staged writes cannot be rolled back: the database uses it to
        /// make the execution of a rule set all or nothing when an action throws (<see cref="Database.ExecuteRuleSet"/>).
        /// O(shards); the mark of an empty state is <c>null</c> and allocates nothing. Must not race a flush or a merge.
        /// </summary>
        /// <returns>The row and removal queue lengths of each shard, interleaved, or <c>null</c> when nothing is
        /// staged.</returns>
        public int[]? Mark()
        {
            if (IsEmpty)
                return null;

            var mark = new int[2 * rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                mark[2 * i] = rows[i].Count;
                mark[2 * i + 1] = removals[i].Count;
            }

            return mark;
        }

        /// <summary>
        /// Discards every buffer staged since <paramref name="mark"/> was taken, returning its storage to the pool, and
        /// recomputes the totals from the buffers that remain, which keep their order: rolls back the writes of an
        /// operation that failed halfway. O(staged buffers). Must not race a flush or a merge, and no merge may have run
        /// since the mark was taken.
        /// </summary>
        /// <param name="mark">A mark from <see cref="Mark"/>; <c>null</c> discards everything, as <see cref="Clear"/>
        /// does.</param>
        /// <exception cref="ArgumentException">The mark does not fit this state's shards.</exception>
        public void DiscardSince(int[]? mark)
        {
            if (mark is null)
            {
                Clear();
                return;
            }

            if (mark.Length != 2 * rows.Length)
                throw new ArgumentException($"The mark covers {mark.Length / 2} shards, not {rows.Length}.", nameof(mark));

            long keptRows = 0;
            long keptRemovals = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                keptRows += Truncate(rows[i], mark[2 * i], static buffer => buffer.Count);
                keptRemovals += Truncate(removals[i], mark[2 * i + 1], static buffer => buffer.Count);
            }

            Interlocked.Exchange(ref totalRows, keptRows);
            Interlocked.Exchange(ref totalRemovals, keptRemovals);
        }

        /// <summary>
        /// Keeps the first <paramref name="keep"/> buffers of <paramref name="queue"/>, in order, and disposes the rest.
        /// Cold path.
        /// </summary>
        /// <param name="queue">The queue.</param>
        /// <param name="keep">How many buffers to keep.</param>
        /// <param name="count">Gets the number of rows a buffer holds.</param>
        /// <returns>The number of rows the kept buffers hold.</returns>
        static long Truncate<TBuffer>(ConcurrentQueue<TBuffer> queue, int keep, Func<TBuffer, int> count)
            where TBuffer : IDisposable
        {
            var kept = new List<TBuffer>(Math.Min(keep, queue.Count));
            while (queue.TryDequeue(out var buffer))
            {
                if (kept.Count < keep)
                    kept.Add(buffer);
                else
                    buffer.Dispose();
            }

            long total = 0;
            foreach (var buffer in kept)
            {
                queue.Enqueue(buffer);
                total += count(buffer);
            }

            return total;
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

        /// <summary>
        /// The flagged path of <see cref="EnqueueRows"/>: throws when the state is sealed (disposing the buffer), and
        /// creates a lazy state's row queue for <paramref name="shard"/> on its first publication. Thread-safe: racing
        /// publishers agree on one queue.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void PrepareRows(int shard, RowBuffer buffer)
        {
            if ((flags & SealedFlag) != 0)
            {
                buffer.Dispose();
                ThrowSealed();
            }

            if (ReferenceEquals(Volatile.Read(ref rows[shard]), NoRows) && ReferenceEquals(Interlocked.CompareExchange(ref rows[shard], new ConcurrentQueue<RowBuffer>(), NoRows), NoRows))
                NoteQueueCreated();
        }

        /// <summary>
        /// The flagged path of <see cref="EnqueueRemovals"/>, as <see cref="PrepareRows"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void PrepareRemovals(int shard, ArbitraryRowBuffer buffer)
        {
            if ((flags & SealedFlag) != 0)
            {
                buffer.Dispose();
                ThrowSealed();
            }

            if (ReferenceEquals(Volatile.Read(ref removals[shard]), NoRemovals) && ReferenceEquals(Interlocked.CompareExchange(ref removals[shard], new ConcurrentQueue<ArbitraryRowBuffer>(), NoRemovals), NoRemovals))
                NoteQueueCreated();
        }

        /// <summary>
        /// Counts a lazy queue created; once every queue exists, publications take the unflagged path again.
        /// </summary>
        void NoteQueueCreated()
        {
            if (Interlocked.Decrement(ref missingQueues) == 0)
                Interlocked.And(ref flags, ~LazyFlag);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowSealed()
        {
            throw new InvalidOperationException("The context is frozen.");
        }

    }

}
