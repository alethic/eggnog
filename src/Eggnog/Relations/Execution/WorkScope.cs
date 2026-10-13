using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A structured group of tasks that one thread waits for while helping to run them: the .NET counterpart of rayon's
    /// <c>in_place_scope</c>, which the parallel executor uses for the rules of a rule set, their morsels and action
    /// batches, and the blocks of decomposed plans (core-relations/src/free_join/execute.rs:358-499).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Spawn"/> queues an item and, while fewer than <c>degreeOfParallelism - 1</c> workers are active,
    /// starts a thread-pool worker that drains the scope's queue. <see cref="Wait"/> drains the queue on the calling
    /// thread too, and returns once every spawned item, including items spawned by items, has finished. Because the
    /// waiter helps with its own scope's items, a scope nested inside a task (a decomposed plan's block inside a rule)
    /// cannot starve: its items run on the waiting thread if no worker takes them.
    /// </para>
    /// <para>
    /// At most <c>degreeOfParallelism</c> threads run a scope's items: its workers plus the waiter. Nested scopes have
    /// their own budget. Items run in no particular order.
    /// </para>
    /// <para>
    /// <see cref="IsBacklogged"/> tells producers when the scope already holds several items per thread, so they can do
    /// the next piece of work inline instead of queueing it: the queue (and the memory its items pin) stays bounded,
    /// and a stop request raised by a running item is seen after a bounded amount of queued work. rayon has no such
    /// signal; it changes when work runs, not what runs.
    /// </para>
    /// <para>
    /// The first exception an item throws is rethrown by <see cref="Wait"/> after every item has finished; the other
    /// items still run, as rayon lets the other tasks of a panicking scope finish.
    /// </para>
    /// <para>
    /// Thread-safe. <see cref="Wait"/> must be called once, by the thread that owns the scope.
    /// </para>
    /// </remarks>
    internal sealed class WorkScope
    {

        readonly ConcurrentQueue<ScopedWork> queue = new();
        readonly object gate = new();
        readonly int maxWorkers;
        readonly int backlogLimit;
        int pending;
        int workers;
        int waiters;
        ExceptionDispatchInfo? error;

        /// <summary>
        /// Initializes an empty scope.
        /// </summary>
        /// <param name="degreeOfParallelism">The most threads that may run the scope's items at once, the waiter
        /// included; one runs every item on the waiting thread.</param>
        public WorkScope(int degreeOfParallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(degreeOfParallelism, 1);
            maxWorkers = degreeOfParallelism - 1;
            backlogLimit = BacklogPerThread * degreeOfParallelism;
        }

        /// <summary>
        /// The number of unfinished items per thread above which <see cref="IsBacklogged"/> is <c>true</c>.
        /// </summary>
        public const int BacklogPerThread = 4;

        /// <summary>
        /// Gets the number of items spawned and not yet finished.
        /// </summary>
        public int Pending => Volatile.Read(ref pending);

        /// <summary>
        /// Gets whether more than <see cref="BacklogPerThread"/> items per thread are unfinished, so a producer should
        /// run its next piece of work inline rather than spawn it.
        /// </summary>
        public bool IsBacklogged => Volatile.Read(ref pending) > backlogLimit;

        /// <summary>
        /// Queues <paramref name="work"/> to run on some thread before <see cref="Wait"/> returns. rayon's
        /// <c>Scope::spawn</c>. Lock-free apart from waking a blocked waiter.
        /// </summary>
        /// <param name="work">The work.</param>
        public void Spawn(ScopedWork work)
        {
            ArgumentNullException.ThrowIfNull(work);
            Interlocked.Increment(ref pending);
            queue.Enqueue(work);
            StartWorker();
            WakeWaiters();
        }

        /// <summary>
        /// Runs queued items on the calling thread until every spawned item has finished, then rethrows the first
        /// exception any item threw. The end of rayon's <c>in_place_scope</c>.
        /// </summary>
        public void Wait()
        {
            while (true)
            {
                while (queue.TryDequeue(out var work))
                    Execute(work);

                if (Volatile.Read(ref pending) == 0)
                    break;

                // Items are running elsewhere: sleep until one finishes or a new one is queued. The pulse protocol
                // (waiters is raised before the condition is read, and read after the queue or the count changes)
                // cannot lose a wake-up; the timeout only bounds the damage if it ever did.
                lock (gate)
                {
                    Interlocked.Increment(ref waiters);
                    if (Volatile.Read(ref pending) != 0 && queue.IsEmpty)
                        Monitor.Wait(gate, 10);

                    Interlocked.Decrement(ref waiters);
                }
            }

            error?.Throw();
        }

        /// <summary>
        /// Starts a thread-pool worker if fewer than the maximum are active.
        /// </summary>
        void StartWorker()
        {
            while (true)
            {
                var active = Volatile.Read(ref workers);
                if (active >= maxWorkers)
                    return;

                if (Interlocked.CompareExchange(ref workers, active + 1, active) == active)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(static state => ((WorkScope)state!).WorkerLoop(), this);
                    return;
                }
            }
        }

        /// <summary>
        /// Drains the queue on a worker thread, then retires; looks once more after retiring, since an item queued
        /// meanwhile may have found every worker slot taken.
        /// </summary>
        void WorkerLoop()
        {
            while (true)
            {
                while (queue.TryDequeue(out var work))
                    Execute(work);

                Interlocked.Decrement(ref workers);
                if (queue.IsEmpty)
                    return;

                var active = Volatile.Read(ref workers);
                if (active >= maxWorkers || Interlocked.CompareExchange(ref workers, active + 1, active) != active)
                    return;
            }
        }

        /// <summary>
        /// Runs one item, records its exception, and counts it finished.
        /// </summary>
        void Execute(ScopedWork work)
        {
            try
            {
                work.Run();
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref error, ExceptionDispatchInfo.Capture(e), null);
            }
            finally
            {
                if (Interlocked.Decrement(ref pending) == 0)
                    WakeWaiters();
            }
        }

        /// <summary>
        /// Wakes a waiter blocked in <see cref="Wait"/>, if any.
        /// </summary>
        void WakeWaiters()
        {
            if (Volatile.Read(ref waiters) == 0)
                return;

            lock (gate)
                Monitor.PulseAll(gate);
        }

    }

}
