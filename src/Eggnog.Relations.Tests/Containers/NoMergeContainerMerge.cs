using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The merge of egglog's container unit tests: <c>assert_eq!(v1, v2, "this test shouldn't merge anything")</c>, then
    /// keep the first (core-relations/src/containers/tests.rs:98-101).
    /// </summary>
    sealed class NoMergeContainerMerge : ContainerMergeFunction
    {

        /// <summary>
        /// Gets the shared instance.
        /// </summary>
        public static NoMergeContainerMerge Instance { get; } = new();

        /// <inheritdoc />
        public override uint Merge(ExecutionState state, uint current, uint incoming)
        {
            Assert.True(current == incoming, $"this test shouldn't merge anything, but merged {current} and {incoming}");
            return current;
        }

    }

}
