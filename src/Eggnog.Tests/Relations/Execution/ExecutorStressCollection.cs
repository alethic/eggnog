using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The executor tests that run rule sets over large tables or on several threads. They run on their own, after the
    /// parallel test collections, so that their memory, garbage-collection and thread load does not disturb tests that
    /// measure allocations or timing (as <see cref="IndexStressCollection"/> does for the index tests).
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ExecutorStressCollection
    {

        /// <summary>
        /// The collection's name.
        /// </summary>
        public const string Name = "Executor stress";

    }

}
