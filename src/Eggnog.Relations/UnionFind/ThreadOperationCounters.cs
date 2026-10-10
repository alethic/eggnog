using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// One counter per thread, odd exactly while that thread is inside a <see cref="ConcurrentUnionFind"/> operation,
    /// so that <see cref="ConcurrentUnionFind.Reset"/> can wait for operations in flight. This plays the part of the
    /// read guards of egglog's <c>ReadOptimizedLock</c> (<c>concurrency/src/lib.rs:33-187</c>), which
    /// <c>concurrent/buffer.rs</c> takes around every union-find operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Protocol. An operation reads its thread's counter (only that thread writes it), stores the next, odd, value
    /// with a release store, then reads the structure's "reset in progress" flag; on the way out it stores the next,
    /// even, value. A reset raises its flag, then calls <see cref="WaitForOperations"/>, which issues a process-wide
    /// memory barrier and waits, for every counter it finds odd, until that counter changes. The barrier makes this
    /// an asymmetric Dekker handshake: the .NET memory model guarantees that every thread effectively executes a full
    /// fence at some point synchronized with it, so either the operation's odd store is visible to the scan that
    /// follows the barrier, and the reset waits for the operation, or the operation's read of the flag sees it
    /// raised, and the operation backs off. Operations therefore pay no interlocked instruction and touch no shared
    /// cache line beyond the flag; the reset pays the barrier, which interrupts every core.
    /// </para>
    /// <para>
    /// Waiting for a change rather than for an even value means a thread that keeps starting operations (on other
    /// union-finds, which share these counters) cannot starve a reset: any change shows that the operation seen in
    /// flight has finished. Operations are short and never block inside the bracket, so the wait is short too.
    /// </para>
    /// <para>
    /// Layout and lifetime. Each counter sits in the middle of its own <see cref="CellLength"/>-element array, 64 bytes
    /// from either end, so no two threads' counters, and no unrelated object, share its cache line. Cells are indexed
    /// by managed thread identifier. The runtime gives a new thread the identifier of one that has exited, and the new
    /// thread takes over that cell; the old thread left it even, since no operation can throw or block between its
    /// two stores. The registry therefore never exceeds the largest managed thread identifier that ran an operation.
    /// </para>
    /// </remarks>
    internal static class ThreadOperationCounters
    {

        /// <summary>
        /// The number of <see cref="uint"/>s in a cell: 128 bytes, a cache line pair.
        /// </summary>
        const int CellLength = 32;

        /// <summary>
        /// The index of the counter within its cell.
        /// </summary>
        const int CounterIndex = CellLength / 2;

        /// <summary>
        /// Serializes registration.
        /// </summary>
        static readonly object registryLock = new();

        /// <summary>
        /// This thread's cell, once registered.
        /// </summary>
        [ThreadStatic]
        static uint[]? current;

        /// <summary>
        /// Every registered cell, indexed by managed thread identifier. Replaced by a larger copy when an identifier
        /// outgrows it; entries and replacements are published with release stores.
        /// </summary>
        static uint[]?[] cells = new uint[]?[16];

        /// <summary>
        /// Gets the calling thread's counter, registering the thread on first use. Only the calling thread may write
        /// it. Constant time after the first call: one thread-static load.
        /// </summary>
        /// <returns>A reference to the counter.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref uint Current()
        {
            var cell = current ?? Register();

            // In bounds: every cell has CellLength elements.
            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cell), CounterIndex);
        }

        /// <summary>
        /// Waits until every operation that was in flight when the call began has left its bracket. Linear in the
        /// registry's length, plus a process-wide memory barrier, plus the time the slowest such operation takes.
        /// </summary>
        /// <remarks>
        /// The caller must first publish whatever makes new operations back off (the reset flag), with a store that
        /// precedes this call. An operation that misses that store is guaranteed to be seen here, and waited for.
        /// </remarks>
        public static void WaitForOperations()
        {
            Interlocked.MemoryBarrierProcessWide();

            // A thread that registered after this read missed the caller's flag only if its registration and its odd
            // store precede the barrier, in which case the read sees them; otherwise it sees the flag and backs off.
            var all = Volatile.Read(ref cells);
            for (var i = 0; i < all.Length; i++)
            {
                var cell = Volatile.Read(ref all[i]);
                if (cell == null)
                    continue;

                ref var counter = ref cell[CounterIndex];
                var seen = Volatile.Read(ref counter);
                if ((seen & 1) == 0)
                    continue;

                var spinner = new SpinWait();
                while (Volatile.Read(ref counter) == seen)
                    spinner.SpinOnce();
            }
        }

        /// <summary>
        /// Registers the calling thread: takes over the cell of an exited thread that had the same managed thread
        /// identifier, or adds one.
        /// </summary>
        /// <returns>The thread's cell.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static uint[] Register()
        {
            var id = Environment.CurrentManagedThreadId;
            lock (registryLock)
            {
                var all = cells;
                if (id >= all.Length)
                {
                    var grown = new uint[]?[Math.Max(id + 1, all.Length * 2)];
                    Array.Copy(all, grown, all.Length);
                    Volatile.Write(ref cells, grown);
                    all = grown;
                }

                var cell = all[id];
                if (cell == null)
                {
                    cell = new uint[CellLength];
                    Volatile.Write(ref all[id], cell);
                }

                Debug.Assert((cell[CounterIndex] & 1) == 0, "A thread exited inside a union-find operation.");
                current = cell;
                return cell;
            }
        }

    }

}
