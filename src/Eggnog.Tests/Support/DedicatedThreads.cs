using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// Runs a test body on dedicated threads that are all alive at once: unlike <c>Parallel.For</c>, which may run every
    /// iteration inline on the calling thread when the pool is busy, the threads meet at a barrier before the first
    /// iteration, so the calls really overlap (rule-layer §8.3, N24).
    /// </summary>
    internal static class DedicatedThreads
    {

        /// <summary>
        /// How long the threads wait for each other at the start, and how long the caller waits for them to finish.
        /// </summary>
        static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Runs <paramref name="body"/> for every iteration in <c>[0, iterations)</c> on <paramref name="threads"/>
        /// dedicated threads, thread <c>t</c> taking iterations <c>t, t + threads, …</c>, after all of them reached a
        /// barrier.
        /// </summary>
        /// <param name="threads">The number of threads.</param>
        /// <param name="iterations">The number of iterations.</param>
        /// <param name="body">The body, given the iteration.</param>
        /// <returns>The number of distinct threads that ran iterations: <paramref name="threads"/> when every thread had
        /// one.</returns>
        /// <exception cref="AggregateException">An iteration threw; the exceptions of every thread.</exception>
        /// <exception cref="TimeoutException">The threads did not meet or finish in time.</exception>
        public static int For(int threads, int iterations, Action<int> body)
        {
            using var barrier = new Barrier(threads);
            var errors = new ConcurrentQueue<Exception>();
            var ran = new ConcurrentDictionary<int, bool>();
            var started = new Thread[threads];
            for (var t = 0; t < threads; t++)
            {
                var first = t;
                started[t] = new Thread(() =>
                {
                    try
                    {
                        if (barrier.SignalAndWait(Patience) == false)
                            throw new TimeoutException("The threads did not all start.");

                        for (var i = first; i < iterations; i += threads)
                        {
                            ran[Environment.CurrentManagedThreadId] = true;
                            body(i);
                        }
                    }
                    catch (Exception exception)
                    {
                        errors.Enqueue(exception);
                    }
                })
                { IsBackground = true };
                started[t].Start();
            }

            foreach (var thread in started)
                if (thread.Join(Patience) == false)
                    throw new TimeoutException("A thread did not finish.");

            if (errors.IsEmpty == false)
                throw new AggregateException(errors);

            return ran.Count;
        }

    }

}
