using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The hold an extraction query keeps on a context's thread domain: entered, and marked as running a caller's code
    /// (the cost model's functions), for the query's duration, so a second thread fails fast and a cost function cannot
    /// write to the context it is costing. The default scope holds nothing: a frozen context, or an extractor made
    /// through the e-graph's root API, which keeps egglog's unguarded root.
    /// </summary>
    /// <remarks>
    /// A value type so a query allocates nothing for it. Dispose it exactly once, on the thread that entered, also when
    /// the query throws.
    /// </remarks>
    internal readonly struct ExtractionQueryScope : IDisposable
    {

        readonly ThreadAffinityGuard? guard;

        /// <summary>
        /// Initializes a scope over a domain the calling thread has entered and marked as in a callback.
        /// </summary>
        /// <param name="guard">The domain entered.</param>
        internal ExtractionQueryScope(ThreadAffinityGuard guard)
        {
            this.guard = guard;
        }

        /// <summary>
        /// Ends the callback mark and exits the domain; does nothing for the default scope.
        /// </summary>
        public void Dispose()
        {
            if (guard is null)
                return;

            guard.ExitCallback();
            guard.Exit();
        }

    }

}
