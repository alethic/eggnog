using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Exclusive access to the data guarded by a <see cref="ReadOptimizedLock{T}"/>, held until disposed. The Rust's
    /// <c>MutexWriter</c>.
    /// </summary>
    /// <remarks>
    /// Dispose exactly once, typically with a <c>using</c> statement; a copy of the handle shares the same lock and must
    /// not be disposed separately. Disposing reopens the lock and wakes every thread that blocked behind the write.
    /// </remarks>
    /// <typeparam name="T">The type of the guarded data.</typeparam>
    internal readonly struct ReadOptimizedLockWriter<T> : IDisposable
    {

        readonly ReadOptimizedLock<T> owner;
        readonly Notification unblock;

        /// <summary>
        /// Initializes a new instance for a write lock already taken with <paramref name="unblock"/>.
        /// </summary>
        /// <param name="owner">The lock.</param>
        /// <param name="unblock">The notification the lock's writer slot holds.</param>
        internal ReadOptimizedLockWriter(ReadOptimizedLock<T> owner, Notification unblock)
        {
            this.owner = owner;
            this.unblock = unblock;
        }

        /// <summary>
        /// Gets the guarded data, which may be read, mutated or replaced. Valid until the handle is disposed.
        /// </summary>
        public ref T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref owner.GetMutableRef();
        }

        /// <summary>
        /// Releases the write lock.
        /// </summary>
        public void Dispose() => owner.ExitWrite(unblock);

    }

}
