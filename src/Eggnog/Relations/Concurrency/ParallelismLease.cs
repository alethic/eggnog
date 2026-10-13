using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Threads leased from a <see cref="ParallelismBudget"/>, for a <c>using</c> statement: disposing the lease returns
    /// them. The default value holds nothing and its disposal does nothing, which is what a failed
    /// <see cref="ParallelismBudget.TryLease"/> hands out.
    /// </summary>
    /// <remarks>
    /// A value type so leasing allocates nothing. Copies share the lease: dispose exactly one of them, exactly once. A
    /// second disposal is caught by the budget only when it would over-fill it
    /// (<see cref="ParallelismBudget.Release"/>).
    /// </remarks>
    internal readonly struct ParallelismLease : IDisposable
    {

        readonly ParallelismBudget? budget;
        readonly int threads;

        /// <summary>
        /// Initializes a lease of <paramref name="threads"/> threads of <paramref name="budget"/>.
        /// </summary>
        /// <param name="budget">The budget leased from.</param>
        /// <param name="threads">The number of threads leased.</param>
        internal ParallelismLease(ParallelismBudget budget, int threads)
        {
            this.budget = budget;
            this.threads = threads;
        }

        /// <summary>
        /// Gets the budget the lease came from, or <c>null</c> for the default lease.
        /// </summary>
        public ParallelismBudget? Budget => budget;

        /// <summary>
        /// Gets the number of threads leased; zero for the default lease.
        /// </summary>
        public int Threads => threads;

        /// <summary>
        /// Returns the leased threads to the budget; does nothing for the default lease.
        /// </summary>
        public void Dispose() => budget?.Release(threads);

    }

}
