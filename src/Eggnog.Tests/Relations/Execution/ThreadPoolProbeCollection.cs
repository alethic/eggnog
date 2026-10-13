using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The test classes that need the thread pool to start work within a bounded wait: bodies that must run at the same
    /// time on pool threads, or callbacks that must arrive on a pool thread. They run on their own, after the parallel
    /// test collections: the engine's and the library's tests share one process, and the engine's parallel tests can
    /// keep every pool thread busy for longer than such a wait.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ThreadPoolProbeCollection
    {

        /// <summary>
        /// The collection's name.
        /// </summary>
        public const string Name = "Thread-pool probes";

    }

}
