using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A counted pool of threads that concurrent work leases from, so that children of a frozen context run side by
    /// side without oversubscribing the machine. An <see cref="EGraph"/> holds one, sized by its degree of parallelism
    /// (<see cref="EGraph.ParallelismBudget"/>), and
    /// <see cref="ConcurrentChildren.Run(System.Collections.Generic.IReadOnlyList{EGraphContext}, Action{EGraphContext, int})"/>
    /// runs each child's body under a lease of the child's own degree of parallelism.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invariant: <c>0 &lt;= Available &lt;= Total</c>, and <c>Total - Available</c> is the sum of the threads of the
    /// leases not yet released. A lease asks for between one and <see cref="Total"/> threads: a larger request could
    /// never be granted, so it is refused rather than left to block forever.
    /// </para>
    /// <para>
    /// Thread-safe. Every change is made under one private lock, and <see cref="Lease"/> waits on it
    /// (<see cref="Monitor.Wait(object)"/>) until enough threads are released; <see cref="Release"/> wakes every
    /// waiter. Waiters are not served in arrival order: a large request can wait behind smaller ones that keep fitting.
    /// Cost: one uncontended lock per lease and per release, which is negligible against the work a lease covers (a
    /// child's whole body); nothing here is on a per-row path.
    /// </para>
    /// <para>
    /// A thread that holds a lease and then blocks on a second one can deadlock once the budget is exhausted; callers
    /// that nest concurrent work run the nested part under the lease they hold (<see cref="ConcurrentChildren"/> does).
    /// </para>
    /// </remarks>
    internal sealed class ParallelismBudget
    {

        /// <summary>
        /// Guards <see cref="available"/> and is what blocked leases wait on.
        /// </summary>
        readonly object gate = new();

        readonly int total;

        /// <summary>
        /// The threads not leased; written under <see cref="gate"/>, read volatile by <see cref="Available"/>.
        /// </summary>
        int available;

        /// <summary>
        /// Initializes a budget of <paramref name="total"/> threads, all available.
        /// </summary>
        /// <param name="total">The number of threads; at least one.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="total"/> is less than one.</exception>
        public ParallelismBudget(int total)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(total, 1);
            this.total = total;
            available = total;
        }

        /// <summary>
        /// Gets the number of threads the budget holds. Fixed at construction.
        /// </summary>
        public int Total => total;

        /// <summary>
        /// Gets the number of threads not leased right now. A snapshot: other threads may lease or release right after.
        /// </summary>
        public int Available => Volatile.Read(ref available);

        /// <summary>
        /// Leases <paramref name="threads"/> threads, blocking until that many are available. Dispose the returned
        /// lease (or call <see cref="Release"/> with the same count) exactly once.
        /// </summary>
        /// <param name="threads">The number of threads; between one and <see cref="Total"/>.</param>
        /// <returns>The lease.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="threads"/> is less than one or more than
        /// <see cref="Total"/>.</exception>
        public ParallelismLease Lease(int threads)
        {
            CheckCount(threads);
            lock (gate)
            {
                while (available < threads)
                    Monitor.Wait(gate);

                Volatile.Write(ref available, available - threads);
            }

            return new ParallelismLease(this, threads);
        }

        /// <summary>
        /// Leases <paramref name="threads"/> threads if that many are available now, without blocking.
        /// </summary>
        /// <param name="threads">The number of threads; between one and <see cref="Total"/>.</param>
        /// <param name="lease">The lease when the call succeeds; the default (empty) lease otherwise.</param>
        /// <returns><c>true</c> if the threads were leased.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="threads"/> is less than one or more than
        /// <see cref="Total"/>.</exception>
        public bool TryLease(int threads, out ParallelismLease lease)
        {
            CheckCount(threads);
            lock (gate)
            {
                if (available < threads)
                {
                    lease = default;
                    return false;
                }

                Volatile.Write(ref available, available - threads);
            }

            lease = new ParallelismLease(this, threads);
            return true;
        }

        /// <summary>
        /// Returns <paramref name="threads"/> leased threads to the budget and wakes every blocked lease.
        /// <see cref="ParallelismLease.Dispose"/> calls it.
        /// </summary>
        /// <param name="threads">The number of threads to return; at least one.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="threads"/> is less than one.</exception>
        /// <exception cref="InvalidOperationException">More threads would be available than the budget holds: a lease
        /// was released twice, or threads were released that were never leased.</exception>
        public void Release(int threads)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
            lock (gate)
            {
                if (threads > total - available)
                    ThrowOverRelease(threads);

                Volatile.Write(ref available, available + threads);
                Monitor.PulseAll(gate);
            }
        }

        /// <inheritdoc />
        public override string ToString() => $"{Available} of {total} thread(s) available";

        /// <summary>
        /// Checks that a lease of <paramref name="threads"/> can ever be granted.
        /// </summary>
        void CheckCount(int threads)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(threads, total);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowOverRelease(int threads)
        {
            throw new InvalidOperationException($"Releasing {threads} thread(s) would make more than the budget's {total} available ({Available} are); a lease was released twice, or threads were released that were never leased.");
        }

    }

}
