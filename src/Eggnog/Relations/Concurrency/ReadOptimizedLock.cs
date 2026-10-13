using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A lock for data that is read often, in short critical sections, by many threads at once, and written rarely, in
    /// large critical sections. Many readers or one writer hold it at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of egglog's <c>ReadOptimizedLock</c> (concurrency/src/lib.rs:33-204). A read lock on a
    /// <see cref="ReaderWriterLockSlim"/> does a read-modify-write on one shared counter, so the cache line holding it
    /// bounces between cores and short reads stop scaling. egglog avoids this with <c>ArcSwap</c>'s per-thread debt
    /// slots. This port gets the same effect with striped reader counters, one 128-byte stripe per core (capped at
    /// <see cref="MaxStripes"/>): a reader increments the counter of the core it runs on, which no other core usually
    /// touches, then checks that no writer is active.
    /// </para>
    /// <para>
    /// A writer publishes itself by swapping a fresh <see cref="Notification"/> into the writer slot, then waits until
    /// every stripe drains to zero, spinning briefly and then blocking until the last reader wakes it. While a writer
    /// is pending or active, new readers and writers block on its notification, which it fires on release. That is the
    /// Rust's <c>WriteOngoing</c> token, and like the Rust a pending writer holds off new readers. Reader and writer
    /// use a Dekker handshake: the reader's increment and the writer's swap are full fences, each followed by a read of
    /// the other side's state, so they can never both miss each other.
    /// </para>
    /// <para>
    /// Memory ordering matches the Rust: everything a writer did before releasing is visible to every later reader and
    /// writer, and everything a reader did before releasing happens before the next writer's critical section.
    /// </para>
    /// <para>
    /// Cost: a read lock is two interlocked operations on a core-local cache line plus two loads of a shared,
    /// read-mostly field. A write lock allocates one <see cref="Notification"/> and waits for all current readers.
    /// </para>
    /// <para>
    /// The lock is not reentrant, as in the Rust: taking the write lock while holding a read lock on the same thread
    /// deadlocks, and so does taking a second read lock while another thread's write is pending. Because pending
    /// writers hold off new readers, back-to-back writers can starve readers; that suits egglog's rare writes.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type of the guarded data.</typeparam>
    internal sealed class ReadOptimizedLock<T>
    {

        /// <summary>
        /// The number of <see cref="int"/>s between two reader counters: 128 bytes, so two counters never share a
        /// cache line or an adjacent-line prefetch pair.
        /// </summary>
        const int StripeStride = 32;

        /// <summary>
        /// The most reader stripes a lock uses. Bounds the lock's footprint (at most 34 blocks of 128 bytes, padding
        /// included) and the cost of a writer's drain scan on large machines; cores beyond it share stripes.
        /// </summary>
        internal const int MaxStripes = 32;

        /// <summary>
        /// The reader counters. Stripe <c>s</c> lives at index <c>(s + 1) * StripeStride</c>; the first and last
        /// <see cref="StripeStride"/> slots are padding. The array is also the monitor a draining writer waits on.
        /// </summary>
        readonly int[] readers;

        /// <summary>
        /// The number of stripes minus one; the stripe count is a power of two.
        /// </summary>
        readonly int stripeMask;

        /// <summary>
        /// <c>null</c> when reads are allowed; otherwise the notification the pending or active writer fires when it
        /// releases the lock. Read and written only through <see cref="Volatile"/> and <see cref="Interlocked"/>.
        /// </summary>
        Notification? writer;

        T data;

        /// <summary>
        /// Initializes a new instance guarding <paramref name="data"/>.
        /// </summary>
        /// <param name="data">The initial data.</param>
        public ReadOptimizedLock(T data)
        {
            var stripes = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(Environment.ProcessorCount, 1, MaxStripes));
            readers = new int[(stripes + 2) * StripeStride];
            stripeMask = stripes - 1;
            this.data = data;
        }

        /// <summary>
        /// Gets the number of reader stripes.
        /// </summary>
        internal int StripeCount => stripeMask + 1;

        /// <summary>
        /// Takes a read lock, blocking while a write is pending or ongoing. Dispose the returned handle to release it;
        /// the data must not be used after that.
        /// </summary>
        /// <returns>A handle granting shared read access to the data.</returns>
        public ReadOptimizedLockReader<T> Read()
        {
            var slot = ((Thread.GetCurrentProcessorId() & stripeMask) + 1) * StripeStride;
            ref var counter = ref readers[slot];
            while (true)
            {
                // Full fence: the increment is ordered before the read of the writer slot (Dekker with WaitForReaders).
                Interlocked.Increment(ref counter);
                var pending = Volatile.Read(ref writer);
                if (pending is null)
                    return new ReadOptimizedLockReader<T>(this, slot);

                // A writer got in first: back off, let it drain, and retry once it is done.
                ExitRead(slot);
                pending.Wait();
            }
        }

        /// <summary>
        /// Takes the write lock, blocking until any other writer has released it and every current reader has left.
        /// Dispose the returned handle to release it.
        /// </summary>
        /// <returns>A handle granting exclusive access to the data.</returns>
        public ReadOptimizedLockWriter<T> Lock()
        {
            while (true)
            {
                var pending = Volatile.Read(ref writer);
                if (pending is not null)
                {
                    pending.Wait();
                    continue;
                }

                var unblock = new Notification();
                if (Interlocked.CompareExchange(ref writer, unblock, null) is not null)
                    continue;

                WaitForReaders();
                return new ReadOptimizedLockWriter<T>(this, unblock);
            }
        }

        /// <summary>
        /// Gets the data without any synchronization, for a caller that already has exclusive access to the lock (the
        /// Rust's <c>as_mut_ref(&amp;mut self)</c>). Also how the reader and writer handles reach the data.
        /// </summary>
        /// <returns>A reference to the guarded data.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetMutableRef() => ref data;

        /// <summary>
        /// Returns the data without any synchronization, for a caller that is done with the lock (the Rust's
        /// <c>into_inner(self)</c>). The lock must not be used afterwards.
        /// </summary>
        /// <returns>The guarded data.</returns>
        public T IntoInner() => data;

        /// <summary>
        /// Releases a read lock taken on stripe slot <paramref name="slot"/>, and wakes a writer that may be waiting
        /// for readers to drain.
        /// </summary>
        /// <param name="slot">The counter index recorded when the read lock was taken.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ExitRead(int slot)
        {
            // Full fence: the decrement is ordered after the critical section and before the read of the writer slot.
            var remaining = Interlocked.Decrement(ref readers[slot]);
            Debug.Assert(remaining >= 0, "A read lock was released more often than it was taken.");
            if (Volatile.Read(ref writer) is not null)
                WakeWriter();
        }

        /// <summary>
        /// Releases the write lock taken with <paramref name="unblock"/>: reopens the lock to readers, then wakes every
        /// thread that blocked behind the write.
        /// </summary>
        /// <param name="unblock">The notification published by <see cref="Lock"/>.</param>
        internal void ExitWrite(Notification unblock)
        {
            Debug.Assert(ReferenceEquals(Volatile.Read(ref writer), unblock), "The write lock was released by a stale handle.");

            // Release: the critical section's writes become visible before any reader can see the slot empty.
            Volatile.Write(ref writer, null);
            unblock.Notify();
        }

        /// <summary>
        /// Waits until every reader stripe is zero. Spins while that is cheap, then blocks on the counters' monitor;
        /// every reader that releases while a writer is published pulses it (see <see cref="ExitRead"/>).
        /// </summary>
        void WaitForReaders()
        {
            var spinner = new SpinWait();
            while (AnyReaders())
            {
                if (spinner.NextSpinWillYield)
                {
                    BlockForReaders();
                    return;
                }

                spinner.SpinOnce();
            }
        }

        /// <summary>
        /// Blocks until every reader stripe is zero.
        /// </summary>
        /// <remarks>
        /// No wakeup is lost: if this thread reads a stripe before a reader's decrement lands, the writer slot was
        /// published (full fence) before that read and the reader's decrement is a full fence before its read of the
        /// slot, so the reader sees the writer and pulses, which it can only do once this thread is waiting.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void BlockForReaders()
        {
            lock (readers)
            {
                while (AnyReaders())
                    Monitor.Wait(readers);
            }
        }

        /// <summary>
        /// Wakes a writer blocked in <see cref="BlockForReaders"/>, if any.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void WakeWriter()
        {
            lock (readers)
                Monitor.Pulse(readers);
        }

        /// <summary>
        /// Gets whether any reader stripe is nonzero.
        /// </summary>
        /// <returns><c>true</c> if some read lock may still be held.</returns>
        bool AnyReaders()
        {
            var counters = readers;
            var last = (stripeMask + 1) * StripeStride;
            for (var slot = StripeStride; slot <= last; slot += StripeStride)
                if (Volatile.Read(ref counters[slot]) != 0)
                    return true;

            return false;
        }

    }

}
