using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// The rule layer's guard of one thread domain: a <c>ThreadAffinityGuard</c> held for the whole of every scope
    /// call (a host batch, a run, a read), the releases deferred until the domain's current call ends, and whether a
    /// run observer is running. A second thread fails fast instead of interleaving between engine calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Re-entrant on the owning thread. One guard per thread domain: a writable scope shares its guard with every child
    /// created under it, each child of a frozen scope gets a new one, and freezing a scope gives each child subtree that
    /// shares its guard a new one, as the engine does with its own thread domains. Entering and leaving cost one
    /// compare-and-swap or one increment.
    /// </para>
    /// <para>
    /// A release may run on any thread, the one that disposed a scope or ended its last call, and holds the guard for
    /// its engine work, under a release lock. That work is internal and short and runs no user code, so a call that finds
    /// the guard held waits on the release lock and tries again: it fails only when the guard is still held once no
    /// release can hold it, that is, by another call. A <c>Dispose</c> on one thread therefore never makes another
    /// thread's call of the domain fail.
    /// </para>
    /// </remarks>
    internal sealed class ScopeGuard
    {

        readonly Relations.ThreadAffinityGuard guard = new();
        readonly object sync = new();
        readonly object releaseLock = new();
        List<Scope>? deferred;

        /// <summary>
        /// Enters the domain on the calling thread; re-entrant on the owning thread. When the guard is held, waits for any
        /// release in progress on another thread and tries once more.
        /// </summary>
        /// <param name="scope">The scope being called, for the message.</param>
        /// <exception cref="InvalidOperationException">Another thread is using the domain for a call.</exception>
        public void Enter(Scope scope)
        {
            if (TryEnter())
                return;

            // No release holds the guard while this thread holds the release lock: what still holds it is a call.
            lock (releaseLock)
            {
                if (TryEnter())
                    return;
            }

            throw new InvalidOperationException(
                $"Scope '{scope.Path}' is in use on another thread; only children of a frozen scope may run concurrently");
        }

        /// <summary>
        /// Enters the domain on the calling thread unless another thread holds it; re-entrant on the owning thread.
        /// </summary>
        /// <returns>Whether the domain was entered; the caller then calls <see cref="Exit"/>.</returns>
        public bool TryEnter()
        {
            try
            {
                guard.Enter();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Enters the domain for a release unless another thread holds it or another release is in progress: as
        /// <see cref="TryEnter"/>, under the release lock, which a call that finds the guard held waits on.
        /// </summary>
        /// <returns>Whether the domain was entered; the caller then calls <see cref="ExitAfterRelease"/>.</returns>
        public bool TryEnterForRelease()
        {
            if (Monitor.TryEnter(releaseLock) == false)
                return false;

            if (TryEnter())
                return true;

            Monitor.Exit(releaseLock);
            return false;
        }

        /// <summary>
        /// Leaves the domain entered by <see cref="TryEnterForRelease"/>, as <see cref="Exit"/> does; the release lock is
        /// let go before the deferred releases run, so a release never holds two release locks.
        /// </summary>
        public void ExitAfterRelease()
        {
            List<Scope>? releases;
            try
            {
                releases = Leave();
            }
            finally
            {
                Monitor.Exit(releaseLock);
            }

            Run(releases);
        }

        /// <summary>
        /// Leaves the domain once; at depth 0, runs the releases deferred meanwhile, after the domain is released, so
        /// that a release deferred while this thread was leaving is never lost: either this exit takes it, or its scope saw
        /// the domain free.
        /// </summary>
        public void Exit() => Run(Leave());

        /// <summary>
        /// Gets the calling thread's nesting depth in the domain: 0 when it does not hold it.
        /// </summary>
        public int Depth => guard.Depth;

        /// <summary>
        /// Gets whether a thread other than the calling one holds the domain.
        /// </summary>
        public bool IsHeldByAnotherThread => guard.IsHeld && guard.Depth == 0;

        /// <summary>
        /// Gets or sets whether a run observer is running on the owning thread: reads of the run's scopes are then
        /// allowed and every other call is refused.
        /// </summary>
        public bool InObserver { get; set; }

        /// <summary>
        /// Defers the release of <paramref name="scope"/> until the domain's current call ends.
        /// </summary>
        /// <param name="scope">The disposed scope.</param>
        public void DeferRelease(Scope scope)
        {
            lock (sync)
                (deferred ??= new List<Scope>()).Add(scope);
        }

        /// <summary>
        /// Leaves the domain once and, at depth 0, takes the releases deferred meanwhile.
        /// </summary>
        /// <returns>The deferred releases to run; <c>null</c> for none.</returns>
        List<Scope>? Leave()
        {
            var last = guard.Depth == 1;
            guard.Exit();
            if (last == false)
                return null;

            lock (sync)
            {
                var releases = deferred;
                deferred = null;
                return releases;
            }
        }

        /// <summary>
        /// Runs deferred releases.
        /// </summary>
        static void Run(List<Scope>? releases)
        {
            if (releases is not null)
                foreach (var scope in releases)
                    scope.TryRelease();
        }

    }

}
