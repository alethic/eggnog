using System.Threading.Tasks;

namespace Eggnog.Benchmarks.Probes
{

    /// <summary>
    /// A benchmark class whose parallel work can be put on a given task scheduler, so that
    /// <see cref="AllocationProbe"/>'s per-thread count (<see cref="AllocationCountingScheduler"/>) covers it.
    /// </summary>
    internal interface IScheduledBenchmark
    {

        /// <summary>
        /// Gets or sets the scheduler the benchmark's parallel work runs on, or <c>null</c> for the thread pool. Set
        /// before the global setup, which reads it.
        /// </summary>
        TaskScheduler? Scheduler { get; set; }

    }

}
