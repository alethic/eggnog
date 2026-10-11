using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A merge that reports a failure through the execution state whenever two rows with the same key differ (the
    /// bridge's <c>AssertEq</c>), and keeps the current row.
    /// </summary>
    sealed class FailingMerge : MergeFunction
    {

        /// <summary>
        /// Gets the shared instance.
        /// </summary>
        public static FailingMerge Instance { get; } = new();

        FailingMerge()
            : base(true)
        {

        }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            if (current.SequenceEqual(incoming) == false)
                state.ReportFailure($"conflict: [{string.Join(", ", current.ToArray())}] vs [{string.Join(", ", incoming.ToArray())}]");

            return false;
        }

    }

}
