using System;
using System.Diagnostics;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A one-shot event: <see cref="Notify"/> happens at most once (later calls are no-ops), and every current or future
    /// waiter is released by it. Waits can be bounded by a timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of egglog's <c>Notification</c> (concurrency/src/notification.rs), itself modelled on
    /// <c>absl::Notification</c>. The Rust pairs an <c>AtomicBool</c> fast path with a <c>Mutex</c> and
    /// <c>Condvar</c>; here the flag is read with acquire and written with release semantics
    /// (<see cref="Volatile"/>), and the monitor of a private gate object plays the mutex and condition variable.
    /// </para>
    /// <para>
    /// Cost: <see cref="HasBeenNotified"/> and a <see cref="Wait"/> on a notified instance are a single acquire load.
    /// <see cref="Notify"/> takes an uncontended monitor. A blocked wait parks the thread on the monitor.
    /// </para>
    /// <para>
    /// The Rust <c>Drop</c> locks the mutex so that a thread still inside <c>notify</c> leaves before the memory is
    /// freed. The garbage collector makes that unnecessary.
    /// </para>
    /// </remarks>
    internal sealed class Notification
    {

        readonly object gate = new();
        bool notified;

        /// <summary>
        /// Gets whether <see cref="Notify"/> has been called, without blocking. An acquire load: a <c>true</c> result
        /// makes every write the notifier made before <see cref="Notify"/> visible to the caller.
        /// </summary>
        public bool HasBeenNotified => Volatile.Read(ref notified);

        /// <summary>
        /// Blocks until <see cref="Notify"/> has been called. Returns at once if it already has.
        /// </summary>
        public void Wait()
        {
            if (Volatile.Read(ref notified))
                return;

            lock (gate)
            {
                while (Volatile.Read(ref notified) == false)
                    Monitor.Wait(gate);
            }
        }

        /// <summary>
        /// Blocks until <see cref="Notify"/> has been called or <paramref name="timeout"/> has elapsed.
        /// </summary>
        /// <remarks>
        /// egglog restarts the full timeout after every wakeup of its condition variable (notification.rs:57-70), so a
        /// spurious wakeup can extend the wait. This port waits against a deadline fixed on entry, and re-checks the
        /// flag before reporting a timeout.
        /// </remarks>
        /// <param name="timeout">The longest time to wait; <see cref="Timeout.InfiniteTimeSpan"/> waits forever.</param>
        /// <returns><c>true</c> if notified; <c>false</c> if the timeout elapsed first.</returns>
        public bool WaitWithTimeout(TimeSpan timeout)
        {
            if (Volatile.Read(ref notified))
                return true;

            if (timeout == Timeout.InfiniteTimeSpan)
            {
                Wait();
                return true;
            }

            if (timeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must not be negative.");

            var start = Stopwatch.GetTimestamp();
            lock (gate)
            {
                while (Volatile.Read(ref notified) == false)
                {
                    var remaining = timeout - Stopwatch.GetElapsedTime(start);
                    if (remaining <= TimeSpan.Zero)
                        return false;

                    // Monitor.Wait takes at most int.MaxValue milliseconds; a longer wait simply loops.
                    var milliseconds = (int)Math.Min(Math.Ceiling(remaining.TotalMilliseconds), int.MaxValue);
                    Monitor.Wait(gate, milliseconds);
                }

                return true;
            }
        }

        /// <summary>
        /// Releases every thread blocked in <see cref="Wait"/> or <see cref="WaitWithTimeout"/>, and lets every future
        /// wait return at once. Writes made before this call are visible to any thread that observes the notification.
        /// Calling it again has no effect.
        /// </summary>
        public void Notify()
        {
            lock (gate)
            {
                Volatile.Write(ref notified, true);
                Monitor.PulseAll(gate);
            }
        }

    }

}
