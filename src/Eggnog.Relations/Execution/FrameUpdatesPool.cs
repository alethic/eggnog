using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recycles the frame buffers that a parallel run hands from producers to morsel tasks: a producer takes a buffer
    /// here to replace the one it handed off, and a task returns its buffer once it has replayed it. egglog leaves the
    /// producer an empty <c>Vec</c> that regrows to the morsel size for every morsel; recycling keeps a buffer's grown
    /// capacity instead.
    /// </summary>
    /// <remarks>
    /// Shared by every task of a run; thread-safe and lock-free. Keeps at most <see cref="MaxPooled"/> buffers; the
    /// rest are left to the garbage collector.
    /// </remarks>
    internal sealed class FrameUpdatesPool
    {

        /// <summary>
        /// The most buffers the pool keeps.
        /// </summary>
        public const int MaxPooled = 256;

        readonly ConcurrentQueue<FrameUpdates> free = new();
        int count;

        /// <summary>
        /// Takes an empty buffer, recycled when one is available.
        /// </summary>
        /// <returns>The buffer.</returns>
        public FrameUpdates Rent()
        {
            if (free.TryDequeue(out var updates))
            {
                Interlocked.Decrement(ref count);
                return updates;
            }

            return new FrameUpdates();
        }

        /// <summary>
        /// Takes back a buffer whose frames have all been replayed (or abandoned); it is emptied.
        /// </summary>
        /// <param name="updates">The buffer, which the caller must not use afterwards.</param>
        public void Return(FrameUpdates updates)
        {
            ArgumentNullException.ThrowIfNull(updates);
            updates.Clear();
            if (Interlocked.Increment(ref count) > MaxPooled)
            {
                Interlocked.Decrement(ref count);
                return;
            }

            free.Enqueue(updates);
        }

    }

}
