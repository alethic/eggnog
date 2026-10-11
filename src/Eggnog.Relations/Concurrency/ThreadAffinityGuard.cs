using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// Admits one thread at a time, re-entrantly, and refuses a second thread instead of waiting: the "thread domain"
    /// of colored contexts (colored-contexts spec §8.3, X15). A context that is not frozen shares one guard with every
    /// descendant created under it, so the whole non-frozen chain behaves as a single thread; each child of a frozen
    /// context gets a guard of its own, which is what lets siblings under a frozen parent run concurrently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Enter"/> on the owning thread only deepens the hold. On another thread it claims the guard with one
    /// compare-and-swap of the owner from zero, or throws <see cref="InvalidOperationException"/> when a different
    /// thread holds it: two threads on one domain is a usage error the engine reports rather than serializes, because
    /// waiting would hide it (and could deadlock a caller that holds another domain).
    /// <see cref="Exit"/> releases at depth zero.
    /// </para>
    /// <para>
    /// The owner is a managed thread id (never zero); the depth is read and written only by the owning thread.
    /// Claiming is an interlocked compare-exchange and releasing a volatile write, so everything one holder wrote
    /// happens-before the next holder's reads. Cost: one volatile read and one compare for a re-entrant enter, one
    /// interlocked operation for a first enter.
    /// </para>
    /// </remarks>
    internal sealed class ThreadAffinityGuard
    {

        /// <summary>
        /// The managed thread id of the holder, or zero when the guard is free.
        /// </summary>
        int owner;

        /// <summary>
        /// How many times the holder has entered without exiting; only the holder reads or writes it.
        /// </summary>
        int depth;

        /// <summary>
        /// How many callbacks (a caller's code run inside an operation of the domain) the holder is inside; only the
        /// holder reads or writes it, and it is zero whenever the guard is free.
        /// </summary>
        int callbacks;

        /// <summary>
        /// Gets how deeply the calling thread holds the guard: zero when it does not hold it (whether the guard is free
        /// or held by another thread). Structural context operations refuse to run when this is positive, because they
        /// would then run inside another operation of the same domain (§8.3).
        /// </summary>
        public int Depth
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref owner) == Environment.CurrentManagedThreadId ? depth : 0;
        }

        /// <summary>
        /// Gets whether any thread holds the guard. A snapshot: another thread may claim or release it right after.
        /// </summary>
        public bool IsHeld => Volatile.Read(ref owner) != 0;

        /// <summary>
        /// Gets whether the calling thread holds the guard and is running a caller's code inside one of the domain's
        /// operations (a write batch's action, a <c>ScanMerges</c> visitor). Operations that change a context of the
        /// domain refuse to run while this is true (§8.3). <c>false</c> on a thread that does not hold the guard.
        /// </summary>
        public bool InCallback
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref owner) == Environment.CurrentManagedThreadId && callbacks > 0;
        }

        /// <summary>
        /// Enters the guard on the calling thread: re-entrantly if the thread holds it already, otherwise by claiming
        /// it. Dispose the returned scope (or call <see cref="Exit"/>) once per enter.
        /// </summary>
        /// <returns>The scope whose disposal exits.</returns>
        /// <exception cref="InvalidOperationException">Another thread holds the guard.</exception>
        public ThreadAffinityScope Enter()
        {
            var me = Environment.CurrentManagedThreadId;
            if (Volatile.Read(ref owner) == me)
            {
                depth++;
                return new ThreadAffinityScope(this);
            }

            if (Interlocked.CompareExchange(ref owner, me, 0) != 0)
                ThrowBusy();

            depth = 1;
            return new ThreadAffinityScope(this);
        }

        /// <summary>
        /// Exits one level of the calling thread's hold, releasing the guard when the depth reaches zero.
        /// </summary>
        /// <exception cref="InvalidOperationException">The calling thread does not hold the guard.</exception>
        public void Exit()
        {
            if (Volatile.Read(ref owner) != Environment.CurrentManagedThreadId)
                ThrowNotHeld();

            if (--depth == 0)
            {
                Debug.Assert(callbacks == 0, "a thread domain was released inside a callback");
                Volatile.Write(ref owner, 0);
            }
        }

        /// <summary>
        /// Marks the start of a caller's code run inside an operation of the domain: until the matching
        /// <see cref="ExitCallback"/>, <see cref="InCallback"/> is true on the holding thread. Nests.
        /// </summary>
        /// <exception cref="InvalidOperationException">The calling thread does not hold the guard.</exception>
        public void EnterCallback()
        {
            if (Volatile.Read(ref owner) != Environment.CurrentManagedThreadId)
                ThrowNotHeld();

            callbacks++;
        }

        /// <summary>
        /// Marks the end of a caller's code run that <see cref="EnterCallback"/> started. Call it in a <c>finally</c>, on
        /// the thread that entered, before the enclosing hold is exited.
        /// </summary>
        /// <exception cref="InvalidOperationException">The calling thread does not hold the guard, or is not inside a
        /// callback.</exception>
        public void ExitCallback()
        {
            if (Volatile.Read(ref owner) != Environment.CurrentManagedThreadId)
                ThrowNotHeld();

            if (callbacks == 0)
                throw new InvalidOperationException("The calling thread is not inside a callback of this thread domain.");

            callbacks--;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowBusy()
        {
            throw new InvalidOperationException("A context's thread domain was used from two threads at once; only children of a frozen context may run concurrently.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNotHeld()
        {
            throw new InvalidOperationException("The calling thread does not hold this thread domain.");
        }

    }

}
