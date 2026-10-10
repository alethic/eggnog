using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// One thread's share of an <see cref="ObjectPool{T, TPolicy}"/>: a stack of cleared objects plus the bytes they
    /// are charged. egglog's <c>PoolState</c> (core-relations/src/pool/mod.rs:167-208).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the owning thread pushes and pops, so the state is almost never contended. It still carries a one-word
    /// spin guard because <see cref="ObjectPool{T, TPolicy}.Clear"/> empties every thread's state from whichever
    /// thread calls it; the guard costs one uncontended compare-exchange per operation on a cache line the owning
    /// thread already holds.
    /// </para>
    /// <para>
    /// Invariant: every stored object has been cleared and admitted by its policy, <c>bytes</c> is the sum of the
    /// stored sizes, and <c>bytes</c> never exceeds the pool's limit.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The pooled type.</typeparam>
    internal sealed class ObjectPoolState<T>
        where T : class
    {

        T?[] items = Array.Empty<T?>();
        long[] sizes = Array.Empty<long>();
        int count;
        long bytes;
        int busy;

        /// <summary>
        /// Gets the number of stored objects. Read it only between <see cref="Enter"/> and <see cref="Exit"/>, or
        /// from the owning thread for diagnostics.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets the bytes charged for the stored objects.
        /// </summary>
        public long Bytes => bytes;

        /// <summary>
        /// Acquires the guard. Spins only while another thread is clearing this state.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enter()
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                EnterContended();
        }

        /// <summary>
        /// Releases the guard.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Exit()
        {
            Debug.Assert(busy == 1);
            Volatile.Write(ref busy, 0);
        }

        /// <summary>
        /// Removes and returns the most recently stored object, or <c>null</c> when empty. Call under the guard.
        /// O(1).
        /// </summary>
        /// <returns>The object, or <c>null</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T? Pop()
        {
            if (count == 0)
                return null;

            var i = --count;
            var item = items[i];
            items[i] = null;
            bytes -= sizes[i];
            return item;
        }

        /// <summary>
        /// Stores <paramref name="item"/> unless that would push the charged bytes past <paramref name="limit"/>.
        /// Call under the guard. Amortized O(1).
        /// </summary>
        /// <param name="item">A cleared, admitted object.</param>
        /// <param name="itemBytes">Its size estimate.</param>
        /// <param name="limit">The pool's byte limit.</param>
        /// <returns><c>true</c> if stored; <c>false</c> if dropped for the limit.</returns>
        public bool Push(T item, long itemBytes, long limit)
        {
            Debug.Assert(itemBytes >= 0);
            Debug.Assert(bytes <= limit);

            // Rust: `if self.bytes + item.bytes() > self.limit { return; }`, rearranged so it cannot overflow.
            if (itemBytes > limit - bytes)
                return false;

            if (count == items.Length)
                GrowStack();

            items[count] = item;
            sizes[count] = itemBytes;
            count++;
            bytes += itemBytes;
            return true;
        }

        /// <summary>
        /// Gets whether <paramref name="item"/> is stored here. Linear; used by debug assertions against returning an
        /// object twice. Call under the guard.
        /// </summary>
        /// <param name="item">The object.</param>
        /// <returns><c>true</c> if stored.</returns>
        public bool Contains(T item)
        {
            for (var i = 0; i < count; i++)
                if (ReferenceEquals(items[i], item))
                    return true;

            return false;
        }

        /// <summary>
        /// Drops every stored object and releases the stack's own arrays. egglog's <c>clear_and_shrink</c>. Call under
        /// the guard.
        /// </summary>
        public void ClearAndShrink()
        {
            items = Array.Empty<T?>();
            sizes = Array.Empty<long>();
            count = 0;
            bytes = 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void EnterContended()
        {
            var spin = new SpinWait();
            while (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                spin.SpinOnce();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void GrowStack()
        {
            var length = Math.Max(4, items.Length * 2);
            Array.Resize(ref items, length);
            Array.Resize(ref sizes, length);
        }

    }

}
