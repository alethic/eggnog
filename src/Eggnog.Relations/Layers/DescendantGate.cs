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
    /// <para>
    /// Thread-safe: <see cref="Enter"/> and <see cref="Exit"/> are interlocked, and <see cref="HasLiveDescendants"/>
    /// is one volatile read, which is what the per-merge compaction check pays. The count is exact once every
    /// concurrent creation and disposal has returned.
    /// </para>
    /// <para>
    /// Change tracking. Creating or disposing a descendant changes both the count and the context tree (a child list,
    /// a disposed flag), in separate steps. Each such change is bracketed by <see cref="BeginChange"/> and
    /// <see cref="EndChange"/> on every gate it affects, so a reader that must compare the count with the tree (the
    /// invariant checker, §12.7 item 7) can tell whether it saw one consistent state: it reads <see cref="Changes"/>,
    /// requires <see cref="IsChanging"/> to be false before and after its reads, and <see cref="Changes"/> to be
    /// unchanged after them. Under a frozen context descendants come and go on many threads, so such a reader may have
    /// to retry. Every operation is interlocked (sequentially consistent); cost: three more interlocked operations per
    /// gate per creation or disposal.
    /// </para>
    /// </remarks>
    internal sealed class DescendantGate
    {

        int live;

        /// <summary>
        /// The number of creations or disposals below the gate's context that have begun and not ended.
        /// </summary>
        int changing;

        /// <summary>
        /// The number of creations or disposals below the gate's context that have ended.
        /// </summary>
        long changes;

        /// <summary>
        /// Gets whether any descendant is live. One volatile read.
        /// </summary>
        public bool HasLiveDescendants => Volatile.Read(ref live) > 0;

        /// <summary>
        /// Gets the number of live descendants, at every depth.
        /// </summary>
        public int LiveDescendants => Volatile.Read(ref live);

        /// <summary>
        /// Gets the number of creations or disposals of descendants that have ended. Read it before and after reading the
        /// tree; together with <see cref="IsChanging"/> it tells whether the reads saw one consistent state.
        /// </summary>
        internal long Changes => Interlocked.Read(ref changes);

        /// <summary>
        /// Gets whether a creation or disposal of a descendant is under way. Interlocked, so it is ordered after every
        /// earlier read of the caller.
        /// </summary>
        internal bool IsChanging => Interlocked.CompareExchange(ref changing, 0, 0) != 0;

        /// <summary>
        /// Records a new live descendant.
        /// </summary>
        public void Enter() => Interlocked.Increment(ref live);

        /// <summary>
        /// Marks the start of a creation or disposal of a descendant: called before the context tree changes. Pair it
        /// with <see cref="EndChange"/> in a <c>finally</c>.
        /// </summary>
        internal void BeginChange() => Interlocked.Increment(ref changing);

        /// <summary>
        /// Marks the end of a creation or disposal of a descendant that <see cref="BeginChange"/> started: called after
        /// the context tree and the count have changed.
        /// </summary>
        internal void EndChange()
        {
            Interlocked.Increment(ref changes);
            Interlocked.Decrement(ref changing);
        }

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
