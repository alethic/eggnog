using System;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// Counts, per action, the matches that survived the action's program: egglog's <c>MatchCounter</c>
    /// (core-relations/src/free_join/execute.rs:2247-2264).
    /// </summary>
    /// <remarks>
    /// One counter per action, each on its own 128-byte stretch of one array so that threads counting different actions
    /// never share a cache line (egglog's <c>CachePadded&lt;AtomicUsize&gt;</c>). Incremented once per action batch
    /// with an interlocked add; read after every batch has run.
    /// </remarks>
    internal sealed class MatchCounter
    {

        /// <summary>
        /// The distance between two counters, in <see cref="long"/>s: 128 bytes.
        /// </summary>
        const int Stride = 16;

        readonly long[] counts;
        readonly int count;

        /// <summary>
        /// Initializes counters for <paramref name="actions"/> actions, all zero.
        /// </summary>
        /// <param name="actions">The number of actions.</param>
        public MatchCounter(int actions)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(actions);
            count = actions;
            counts = new long[(actions + 1) * Stride];
        }

        /// <summary>
        /// Adds <paramref name="matches"/> to the counter of <paramref name="action"/>. Thread-safe.
        /// </summary>
        /// <param name="action">The action's index.</param>
        /// <param name="matches">The number of surviving matches of one batch.</param>
        public void Add(int action, int matches)
        {
            if ((uint)action >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(action));

            if (matches != 0)
                Interlocked.Add(ref counts[(action + 1) * Stride], matches);
        }

        /// <summary>
        /// Gets the counter of <paramref name="action"/>.
        /// </summary>
        /// <param name="action">The action's index.</param>
        /// <returns>The total so far.</returns>
        public long Read(int action)
        {
            if ((uint)action >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(action));

            return Volatile.Read(ref counts[(action + 1) * Stride]);
        }

    }

}
