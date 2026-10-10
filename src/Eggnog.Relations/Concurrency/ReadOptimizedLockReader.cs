using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Shared read access to the data guarded by a <see cref="ReadOptimizedLock{T}"/>, held until disposed. The
    /// Rust's <c>MutexReader</c>.
    /// </summary>
    /// <remarks>
    /// Dispose exactly once, typically with a <c>using</c> statement; a copy of the handle shares the same lock and must
    /// not be disposed separately. Holding it blocks writers, so keep it short.
    /// </remarks>
    /// <typeparam name="T">The type of the guarded data.</typeparam>
    internal readonly struct ReadOptimizedLockReader<T> : IDisposable
    {

        readonly ReadOptimizedLock<T> owner;
        readonly int slot;

        /// <summary>
        /// Initializes a new instance for a read lock already taken on <paramref name="slot"/>.
        /// </summary>
        /// <param name="owner">The lock.</param>
        /// <param name="slot">The reader counter that was incremented.</param>
        internal ReadOptimizedLockReader(ReadOptimizedLock<T> owner, int slot)
        {
            this.owner = owner;
            this.slot = slot;
        }

        /// <summary>
        /// Gets the guarded data. Valid until the handle is disposed.
        /// </summary>
        public ref readonly T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref owner.GetMutableRef();
        }

        /// <summary>
        /// Releases the read lock.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() => owner.ExitRead(slot);

    }

}
