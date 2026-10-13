using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Allocates values in a <see cref="SharedArena"/> on behalf of the one thread that created it. The Rust's
    /// <c>Handle</c>.
    /// </summary>
    /// <remarks>
    /// The Rust makes handles <c>!Send</c> and <c>!Sync</c> so that each one's local arena is only ever touched by one
    /// thread, which lets allocation skip synchronization. C# cannot express that statically, so <see cref="Alloc"/>
    /// checks the calling thread and throws <see cref="InvalidOperationException"/> on any other.
    /// </remarks>
    internal sealed class SharedArenaHandle
    {

        readonly SharedArena arena;
        readonly int ownerThreadId;

        /// <summary>
        /// The disposable values allocated through this handle, in allocation order. Touched only by the owning thread
        /// while allocating, then by <see cref="SharedArena.Dispose"/> after every worker is done.
        /// </summary>
        List<IDisposable>? disposables;

        /// <summary>
        /// Initializes a new instance for <paramref name="arena"/>, bound to the calling thread.
        /// </summary>
        /// <param name="arena">The arena.</param>
        internal SharedArenaHandle(SharedArena arena)
        {
            this.arena = arena;
            ownerThreadId = Environment.CurrentManagedThreadId;
        }

        /// <summary>
        /// Allocates <paramref name="value"/> in the arena. If it implements <see cref="IDisposable"/>, the arena's copy
        /// is disposed when the arena is.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="value">The value.</param>
        /// <returns>A shareable read-only reference to the arena's copy.</returns>
        public SharedRef<T> Alloc<T>(T value)
        {
            if (Environment.CurrentManagedThreadId != ownerThreadId)
                ThrowWrongThread();

            ObjectDisposedException.ThrowIf(arena.IsDisposed, arena);

            // A value type is boxed once; the box is the arena's copy, so disposal reaches it in place.
            object? storage = value;
            if (storage is IDisposable disposable)
                (disposables ??= new List<IDisposable>()).Add(disposable);

            return new SharedRef<T>(storage);
        }

        /// <summary>
        /// Disposes the disposable values allocated through this handle, last-in first-out, collecting failures.
        /// </summary>
        /// <param name="failures">Receives the exceptions thrown, created on the first one.</param>
        internal void DisposeValues(ref List<Exception>? failures)
        {
            var list = disposables;
            if (list is null)
                return;

            disposables = null;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                try
                {
                    list[i].Dispose();
                }
                catch (Exception e)
                {
                    (failures ??= new List<Exception>()).Add(e);
                }
            }
        }

        /// <summary>
        /// Throws for an allocation from a thread other than the owner.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowWrongThread()
        {
            throw new InvalidOperationException($"A shared arena handle created on thread {ownerThreadId} was used on thread {Environment.CurrentManagedThreadId}; create one handle per thread.");
        }

    }

}
