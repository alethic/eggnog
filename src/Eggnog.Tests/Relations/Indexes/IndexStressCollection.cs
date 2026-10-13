using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The index tests that build large tables or run many threads. They run on their own, after the parallel test
    /// collections, so that their memory and thread load does not disturb tests that measure allocations or timing.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class IndexStressCollection
    {

        /// <summary>
        /// The collection's name.
        /// </summary>
        public const string Name = "Index stress";

    }

}
