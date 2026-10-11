using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One enter of a <see cref="ThreadAffinityGuard"/>, for a <c>using</c> statement: disposing it exits once. The
    /// default value guards nothing and its disposal does nothing, so a caller that enters only conditionally (a frozen
    /// context takes no guard) can still write a single <c>using</c>.
    /// </summary>
    /// <remarks>
    /// A value type so entering allocates nothing. Dispose each scope exactly once, on the thread that entered.
    /// </remarks>
    internal readonly struct ThreadAffinityScope : IDisposable
    {

        readonly ThreadAffinityGuard? guard;

        /// <summary>
        /// Initializes a scope that exits <paramref name="guard"/> when disposed.
        /// </summary>
        /// <param name="guard">The guard entered.</param>
        internal ThreadAffinityScope(ThreadAffinityGuard guard)
        {
            this.guard = guard;
        }

        /// <summary>
        /// Exits the guard once; does nothing for the default scope.
        /// </summary>
        public void Dispose() => guard?.Exit();

    }

}
