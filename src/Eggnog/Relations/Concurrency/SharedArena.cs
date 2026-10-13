using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A scoped arena for values that parallel workers allocate and then share read-only. Share the arena with the
    /// workers, create one <see cref="SharedArenaHandle"/> inside each, and allocate through it; the resulting
    /// <see cref="SharedRef{T}"/>s may be read from any thread. Disposing the arena disposes every
    /// <see cref="IDisposable"/> value allocated in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of egglog's <c>SharedArena</c> (concurrency/src/shared_arena.rs), which gives each handle its own bump
    /// arena (<c>bumpalo</c>) so that allocation needs no synchronization, and runs destructors when the arena is
    /// dropped. Under the garbage collector, allocation already is a thread-local bump of the allocating thread's
    /// allocation context, and memory is reclaimed when unreachable, so a handle stores nothing but its list of
    /// disposables. Reference-type values are stored as they are; value types are boxed once, and the box is the
    /// arena's copy.
    /// </para>
    /// <para>
    /// The Rust drops values last-in first-out within a handle and leaves the order across handles unspecified; this
    /// disposes handles in creation order and each handle's values last-in first-out. Every value is disposed even if
    /// one throws; the exceptions are rethrown together afterwards.
    /// </para>
    /// <para>
    /// The Rust ties every <c>SharedRef</c>'s lifetime to the arena. Here a <see cref="SharedRef{T}"/> stays readable
    /// after the arena is disposed (the collector keeps the value alive), but a disposable value has been disposed by
    /// then.
    /// </para>
    /// </remarks>
    internal sealed class SharedArena : IDisposable
    {

        /// <summary>
        /// Every handle created so far; also the lock that guards itself and <see cref="disposed"/>.
        /// </summary>
        readonly List<SharedArenaHandle> handles = new();

        bool disposed;

        /// <summary>
        /// Gets whether the arena has been disposed.
        /// </summary>
        internal bool IsDisposed => Volatile.Read(ref disposed);

        /// <summary>
        /// Creates an allocation handle bound to the calling thread. Cheap; create it inside the worker that will use
        /// it.
        /// </summary>
        /// <returns>The handle.</returns>
        public SharedArenaHandle NewHandle()
        {
            lock (handles)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var handle = new SharedArenaHandle(this);
                handles.Add(handle);
                return handle;
            }
        }

        /// <summary>
        /// Disposes every disposable value allocated in the arena: handles in creation order, values within a handle
        /// last-in first-out. Every worker must be done allocating. Later calls do nothing.
        /// </summary>
        public void Dispose()
        {
            SharedArenaHandle[] snapshot;
            lock (handles)
            {
                if (disposed)
                    return;

                Volatile.Write(ref disposed, true);
                snapshot = handles.ToArray();
                handles.Clear();
            }

            List<Exception>? failures = null;
            foreach (var handle in snapshot)
                handle.DisposeValues(ref failures);

            if (failures is not null)
                throw new AggregateException("Disposing values allocated in a shared arena failed.", failures);
        }

    }

}
