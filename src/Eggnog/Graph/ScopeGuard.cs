using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The rule layer's guard of one thread domain (D47): a <c>ThreadAffinityGuard</c> held for the whole of every scope
    /// call (a host batch, a run, a read), the releases deferred until the domain's current call ends (D40), and whether a
    /// run observer is running. A second thread fails fast instead of interleaving between engine calls.
    /// </summary>
    /// <remarks>
    /// Re-entrant on the owning thread. Every milestone keeps one guard per thread domain; in R1 the root's is the only
    /// one. Entering and leaving cost one compare-and-swap or one increment.
    /// </remarks>
    internal sealed class ScopeGuard
    {

        readonly Relations.ThreadAffinityGuard guard = new();
        readonly object sync = new();
        List<Scope>? deferred;

        /// <summary>
        /// Enters the domain on the calling thread; re-entrant on the owning thread.
        /// </summary>
        /// <param name="scope">The scope being called, for the message.</param>
        /// <exception cref="InvalidOperationException">Another thread is using the domain.</exception>
        public void Enter(Scope scope)
        {
            try
            {
                guard.Enter();
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Scope '{scope.Path}' is in use on another thread; only children of a frozen scope may run concurrently");
            }
        }

        /// <summary>
        /// Leaves the domain once; at depth 0, runs the releases deferred meanwhile.
        /// </summary>
        public void Exit()
        {
            List<Scope>? releases = null;
            if (guard.Depth == 1)
            {
                lock (sync)
                {
                    releases = deferred;
                    deferred = null;
                }
            }

            guard.Exit();
            if (releases is not null)
                foreach (var scope in releases)
                    scope.TryRelease();
        }

        /// <summary>
        /// Gets the calling thread's nesting depth in the domain: 0 when it does not hold it.
        /// </summary>
        public int Depth => guard.Depth;

        /// <summary>
        /// Gets whether a thread other than the calling one holds the domain.
        /// </summary>
        public bool IsHeldByAnotherThread => guard.IsHeld && guard.Depth == 0;

        /// <summary>
        /// Gets or sets whether a run observer is running on the owning thread (R1b): reads of the run's scopes are then
        /// allowed and every other call is refused.
        /// </summary>
        public bool InObserver { get; set; }

        /// <summary>
        /// Defers the release of <paramref name="scope"/> until the domain's current call ends (D40).
        /// </summary>
        /// <param name="scope">The disposed scope.</param>
        public void DeferRelease(Scope scope)
        {
            lock (sync)
                (deferred ??= new List<Scope>()).Add(scope);
        }

    }

}
