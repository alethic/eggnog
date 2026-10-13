using System;
using System.Threading;
using System.Threading.Tasks;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// Runs a body on a new thread of its own and returns its result as a task. Threading tests that wait a bounded time
    /// for another thread to reach a point use it instead of <see cref="Task.Run(Action)"/>: the engine's tests run in
    /// the same process and keep the thread pool busy, so a pool item can start too late for such a wait.
    /// </summary>
    public static class DedicatedThread
    {

        /// <summary>
        /// Starts <paramref name="body"/> on a new background thread.
        /// </summary>
        /// <typeparam name="T">The body's result type.</typeparam>
        /// <param name="body">The work.</param>
        /// <returns>A task completed with the body's result or exception; its continuations run asynchronously.</returns>
        public static Task<T> Run<T>(Func<T> body)
        {
            ArgumentNullException.ThrowIfNull(body);
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    completion.SetResult(body());
                }
                catch (Exception e)
                {
                    completion.SetException(e);
                }
            })
            {
                IsBackground = true,
                Name = "Eggnog.Tests dedicated thread",
            };

            thread.Start();
            return completion.Task;
        }

    }

}
