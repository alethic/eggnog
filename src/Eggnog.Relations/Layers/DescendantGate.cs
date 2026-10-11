using System;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// Counts a context's live descendants (colored-contexts spec §6.8): every <see cref="Context"/> owns one, and
    /// creating a child enters the gate of every ancestor, disposing it exits them. While the count is positive the
    /// context's shared rows must stay where descendants read them: its layers do not compact (C12, X27) and it does
    /// not retract shared rows (decision 1, §6.7), and its database cannot be cloned (§4.3).
    /// </summary>
    /// <remarks>
    /// Thread-safe: <see cref="Enter"/> and <see cref="Exit"/> are interlocked, and <see cref="HasLiveDescendants"/>
    /// is one volatile read, which is what the per-merge compaction check pays. The count is exact once every
    /// concurrent creation and disposal has returned.
    /// </remarks>
    internal sealed class DescendantGate
    {

        int live;

        /// <summary>
        /// Gets whether any descendant is live. One volatile read.
        /// </summary>
        public bool HasLiveDescendants => Volatile.Read(ref live) > 0;

        /// <summary>
        /// Gets the number of live descendants, at every depth.
        /// </summary>
        public int LiveDescendants => Volatile.Read(ref live);

        /// <summary>
        /// Records a new live descendant.
        /// </summary>
        public void Enter() => Interlocked.Increment(ref live);

        /// <summary>
        /// Records that a live descendant was disposed.
        /// </summary>
        /// <exception cref="InvalidOperationException">No descendant was live; the gate is left at zero.</exception>
        public void Exit()
        {
            if (Interlocked.Decrement(ref live) < 0)
            {
                Interlocked.Increment(ref live);
                throw new InvalidOperationException("A descendant gate was exited more often than it was entered.");
            }
        }

    }

}
