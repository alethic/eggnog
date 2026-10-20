using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A task scheduler that runs every task at once on the thread that queues it, one at a time. A parallel loop given
    /// it through <see cref="ParallelOptions.TaskScheduler"/> runs all its iterations on the calling thread, so that
    /// <see cref="System.GC.GetAllocatedBytesForCurrentThread"/> counts the loop's allocations exactly.
    /// </summary>
    internal sealed class InlineTaskScheduler : TaskScheduler
    {

        /// <inheritdoc />
        public override int MaximumConcurrencyLevel => 1;

        /// <inheritdoc />
        protected override void QueueTask(Task task) => TryExecuteTask(task);

        /// <inheritdoc />
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);

        /// <inheritdoc />
        protected override IEnumerable<Task> GetScheduledTasks() => Enumerable.Empty<Task>();

    }

}
