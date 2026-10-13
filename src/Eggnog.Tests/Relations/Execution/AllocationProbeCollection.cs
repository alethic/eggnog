using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The test classes that measure allocation (exact byte counts on the current thread, or pool reuse). They run on
    /// their own, after the parallel test collections: the engine's and the library's tests share one process, and
    /// another test's threads can take arrays from the shared array pool's per-core stacks or trigger a collection that
    /// trims it, which a probe would count as its own allocation.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class AllocationProbeCollection
    {

        /// <summary>
        /// The collection's name.
        /// </summary>
        public const string Name = "Allocation probes";

    }

}
