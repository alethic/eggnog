using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// Drives work the way a host drives children of a frozen context: one task per item, all started at once, at most a
    /// given number running their bodies at a time. A task waiting for its turn awaits the gate and holds no thread.
    /// </summary>
    internal static class HostTasks
    {

        /// <summary>
        /// Runs <paramref name="body"/> for every index in <c>[0, count)</c>, each on a task of its own, at most
        /// <paramref name="atOnce"/> at a time, and waits for every task.
        /// </summary>
        /// <param name="count">The number of tasks.</param>
        /// <param name="atOnce">The most tasks that run their bodies at the same time; at least one.</param>
        /// <param name="body">The body, given its index; it runs on one thread from start to end.</param>
        /// <returns>The exceptions the bodies threw, in index order; empty when none threw.</returns>
        public static Exception[] Run(int count, int atOnce, Action<int> body)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfLessThan(atOnce, 1);
            ArgumentNullException.ThrowIfNull(body);
            using var gate = new SemaphoreSlim(atOnce);
            var tasks = new Task[count];
            for (var i = 0; i < count; i++)
            {
                var index = i;
                tasks[i] = Task.Run(async () =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        body(index);
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
            }

            var errors = new List<Exception>();
            foreach (var task in tasks)
            {
                try
                {
                    task.Wait();
                }
                catch (AggregateException e)
                {
                    errors.AddRange(e.InnerExceptions);
                }
            }

            return errors.ToArray();
        }

    }

}
