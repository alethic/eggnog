using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Eggnog.Benchmarks.Probes
{

    /// <summary>
    /// A task scheduler on dedicated threads that counts the bytes its tasks allocate, each on its own thread with
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/>: an exact count that the workstation and server collectors
    /// report alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The process-wide <see cref="GC.GetTotalAllocatedBytes(bool)"/> is not reliable under the server collector with
    /// dynamic adaptation (the default since .NET 9): a difference of two readings can be off by gigabytes or negative.
    /// Under the workstation collector, and under the server collector with dynamic adaptation off, it agrees with the
    /// per-thread count. Per-thread counts are kept by each thread's allocation context and do not depend on the heaps.
    /// </para>
    /// <para>
    /// <see cref="Count"/> runs a body as a task on this scheduler. The body's parallel loops are counted when their
    /// <see cref="ParallelOptions.TaskScheduler"/> is this scheduler; work queued to the thread pool is not, so
    /// <see cref="Count"/> also reports how many thread-pool work items completed meanwhile, and a count that missed
    /// work shows.
    /// </para>
    /// <para>
    /// A task's bytes are counted on the thread that runs it, from its start to its end. A task run inline inside
    /// another (a loop's waiter running a queued replica) is part of the outer task's count. A thread that is not one of
    /// this scheduler's never runs a task inline, so the thread that waits in <see cref="Count"/> runs none of the work.
    /// </para>
    /// </remarks>
    internal sealed class AllocationCountingScheduler : TaskScheduler, IDisposable
    {

        /// <summary>
        /// Whether the current thread is one of a scheduler's workers.
        /// </summary>
        [ThreadStatic]
        static bool isWorker;

        readonly BlockingCollection<Task> queue = new(new ConcurrentQueue<Task>());
        readonly Thread[] threads;
        long bytes;
        int running;

        /// <summary>
        /// Starts <paramref name="threadCount"/> worker threads.
        /// </summary>
        /// <param name="threadCount">The number of workers: at least the largest degree of parallelism of the counted
        /// loops.</param>
        public AllocationCountingScheduler(int threadCount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);

            threads = new Thread[threadCount];
            for (var i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(Work) { IsBackground = true, Name = $"Allocation counter {i}" };
                threads[i].Start();
            }
        }

        /// <inheritdoc />
        public override int MaximumConcurrencyLevel => threads.Length;

        /// <summary>
        /// Runs <paramref name="body"/> on this scheduler and returns the bytes allocated on this scheduler's threads
        /// meanwhile.
        /// </summary>
        /// <param name="body">The work to count.</param>
        /// <returns>The bytes, and the number of thread-pool work items that completed meanwhile, whose bytes the count
        /// leaves out.</returns>
        public (long Bytes, long PoolWorkItems) Count(Action body)
        {
            ArgumentNullException.ThrowIfNull(body);

            var poolItems = ThreadPool.CompletedWorkItemCount;
            var before = Interlocked.Read(ref bytes);
            Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.None, this).Wait();

            // A task completes before its worker adds its bytes; wait until every worker has.
            var spin = new SpinWait();
            while (Volatile.Read(ref running) != 0)
                spin.SpinOnce();

            return (Interlocked.Read(ref bytes) - before, ThreadPool.CompletedWorkItemCount - poolItems);
        }

        /// <summary>
        /// Stops the workers once the queue is empty.
        /// </summary>
        public void Dispose()
        {
            queue.CompleteAdding();
            foreach (var thread in threads)
                thread.Join();

            queue.Dispose();
        }

        /// <inheritdoc />
        protected override void QueueTask(Task task) => queue.Add(task);

        /// <inheritdoc />
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => isWorker && TryExecuteTask(task);

        /// <inheritdoc />
        protected override IEnumerable<Task> GetScheduledTasks() => queue.ToArray();

        /// <summary>
        /// A worker's loop: runs queued tasks and counts the bytes each allocates.
        /// </summary>
        void Work()
        {
            isWorker = true;
            foreach (var task in queue.GetConsumingEnumerable())
            {
                Interlocked.Increment(ref running);
                var start = GC.GetAllocatedBytesForCurrentThread();
                TryExecuteTask(task);
                Interlocked.Add(ref bytes, GC.GetAllocatedBytesForCurrentThread() - start);
                Interlocked.Decrement(ref running);
            }
        }

    }

}
