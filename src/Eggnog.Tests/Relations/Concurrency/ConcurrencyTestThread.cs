using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A dedicated OS thread whose exception is rethrown by <see cref="Join()"/>, like Rust's
    /// <c>JoinHandle::join().unwrap()</c>. Without it a failed assertion on a raw thread would crash the test host.
    /// </summary>
    sealed class ConcurrencyTestThread
    {

        readonly Thread thread;
        ExceptionDispatchInfo? failure;

        ConcurrencyTestThread(Action body)
        {
            thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception e)
                {
                    failure = ExceptionDispatchInfo.Capture(e);
                }
            });
            thread.IsBackground = true;
        }

        /// <summary>
        /// Starts <paramref name="body"/> on a new thread.
        /// </summary>
        public static ConcurrencyTestThread Start(Action body)
        {
            var t = new ConcurrencyTestThread(body);
            t.thread.Start();
            return t;
        }

        /// <summary>
        /// Starts <paramref name="count"/> threads, passing each its index.
        /// </summary>
        public static ConcurrencyTestThread[] StartMany(int count, Action<int> body)
        {
            var threads = new ConcurrencyTestThread[count];
            for (var i = 0; i < count; i++)
            {
                var index = i;
                threads[i] = Start(() => body(index));
            }

            return threads;
        }

        /// <summary>
        /// Gets the thread's state, which shows whether it is blocked (<see cref="ThreadState.WaitSleepJoin"/>) or has
        /// ended (<see cref="ThreadState.Stopped"/>).
        /// </summary>
        public ThreadState ThreadState => thread.ThreadState;

        /// <summary>
        /// Waits for the thread and rethrows its exception, if any.
        /// </summary>
        public void Join()
        {
            thread.Join();
            failure?.Throw();
        }

        /// <summary>
        /// Waits at most <paramref name="timeout"/> for the thread, and rethrows its exception if it ended.
        /// </summary>
        /// <returns><c>true</c> if the thread ended within the timeout.</returns>
        public bool Join(TimeSpan timeout)
        {
            if (thread.Join(timeout) == false)
                return false;

            failure?.Throw();
            return true;
        }

        /// <summary>
        /// Joins every thread, rethrowing the first exception after all have finished.
        /// </summary>
        public static void JoinAll(IEnumerable<ConcurrencyTestThread> threads)
        {
            ExceptionDispatchInfo? first = null;
            foreach (var t in threads)
            {
                t.thread.Join();
                first ??= t.failure;
            }

            first?.Throw();
        }

    }

}
