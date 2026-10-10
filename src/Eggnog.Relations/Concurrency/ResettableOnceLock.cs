using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// Holds a value that is brought up to date at most once per epoch, by whichever thread first asks for it, and is
    /// then shared read-only until the next <see cref="Reset"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of egglog's <c>ResettableOnceLock</c> (concurrency/src/resettable_oncelock.rs), which core-relations uses
    /// for its lazily refreshed hash indexes (free_join/mod.rs:837-876). The state machine:
    /// </para>
    /// <list type="bullet">
    /// <item>A new lock is "to be updated": <see cref="TryGet"/> returns <c>false</c>.</item>
    /// <item>The first <see cref="GetOrUpdate{TState}"/> runs its update on the value, then moves the lock to "updated".
    /// Concurrent callers block until it finishes and do not run their own update.</item>
    /// <item>Once updated, <see cref="TryGet"/> and <see cref="GetOrUpdate{TState}"/> return the value without
    /// locking.</item>
    /// <item><see cref="Reset"/> moves the lock back to "to be updated". Like the Rust's <c>reset(&amp;mut self)</c> it
    /// requires exclusive access: no other thread may be using the lock or still reading the value.</item>
    /// </list>
    /// <para>
    /// The Rust builds this from <c>std::sync::Once</c> with <c>call_once_force</c>, so an update that panics leaves
    /// the lock "to be updated" and the next caller runs its own update. Here the update runs under a monitor, and an
    /// update that throws likewise leaves the lock "to be updated"; the exception propagates to its caller.
    /// </para>
    /// <para>
    /// <c>Once</c> is not reentrant: a <c>get_or_update</c> from inside the update deadlocks, so the update never runs
    /// twice in one epoch. A C# monitor is reentrant, so this port detects the nested call and throws
    /// <see cref="InvalidOperationException"/> instead of running a second update inside the first.
    /// </para>
    /// <para>
    /// Memory ordering matches <c>Once</c>: the completion flag is published with release semantics after the update
    /// and read with acquire semantics, so a caller that sees the lock updated sees the whole update. Cost: one acquire
    /// load once updated.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The value type.</typeparam>
    internal sealed class ResettableOnceLock<T>
    {

        readonly object gate = new();
        T data;
        bool completed;

        /// <summary>
        /// Initializes a new instance, "to be updated", holding <paramref name="value"/>.
        /// </summary>
        /// <param name="value">The initial value, which the first update will see.</param>
        public ResettableOnceLock(T value)
        {
            data = value;
        }

        /// <summary>
        /// Gets whether the value has been updated since the last reset.
        /// </summary>
        public bool IsCompleted
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref completed);
        }

        /// <summary>
        /// Gets the value if it has been updated since the last reset. The Rust's <c>get</c>.
        /// </summary>
        /// <param name="value">The value, if updated.</param>
        /// <returns><c>true</c> if the value has been updated.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet([MaybeNullWhen(false)] out T value)
        {
            if (Volatile.Read(ref completed))
            {
                value = data;
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Gets the value, first running <paramref name="update"/> on it if this is the first request since the last
        /// reset. At most one update runs per epoch; concurrent callers wait for it.
        /// </summary>
        /// <typeparam name="TState">The type of <paramref name="state"/>.</typeparam>
        /// <param name="state">Passed to <paramref name="update"/>.</param>
        /// <param name="update">Brings the value up to date in place. It must not call
        /// <see cref="GetOrUpdate{TState}"/> on this lock.</param>
        /// <returns>A reference to the updated value, valid until the next reset.</returns>
        /// <exception cref="InvalidOperationException">Called from inside this lock's own update (the Rust
        /// deadlocks).</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly T GetOrUpdate<TState>(TState state, ResettableOnceUpdate<T, TState> update)
        {
            if (Volatile.Read(ref completed))
                return ref data;

            return ref UpdateSlow(state, update);
        }

        /// <summary>
        /// Moves the lock back to "to be updated", keeping the value for the next update to start from. Requires
        /// exclusive access.
        /// </summary>
        public void Reset()
        {
            Volatile.Write(ref completed, false);
        }

        /// <summary>
        /// Runs the update under the monitor unless another thread completed it first.
        /// </summary>
        /// <typeparam name="TState">The type of <paramref name="state"/>.</typeparam>
        /// <param name="state">Passed to <paramref name="update"/>.</param>
        /// <param name="update">Brings the value up to date in place.</param>
        /// <returns>A reference to the updated value.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        ref readonly T UpdateSlow<TState>(TState state, ResettableOnceUpdate<T, TState> update)
        {
            ArgumentNullException.ThrowIfNull(update);

            // The gate is only ever held while an update runs, so holding it here means this call came from inside the
            // update. Checked before locking, because the monitor would let the nested call in.
            if (Monitor.IsEntered(gate))
                ThrowReentrant();

            lock (gate)
            {
                if (Volatile.Read(ref completed) == false)
                {
                    update(ref data, state);

                    // Release: the update's writes are visible to anyone who sees the flag.
                    Volatile.Write(ref completed, true);
                }
            }

            return ref data;
        }

        /// <summary>
        /// Throws for a <see cref="GetOrUpdate{TState}"/> made from inside this lock's update.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowReentrant()
        {
            throw new InvalidOperationException($"A {nameof(ResettableOnceLock<T>)} cannot be read with {nameof(GetOrUpdate)} from inside its own update.");
        }

    }

}
