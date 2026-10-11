using System;
using System.Collections.Concurrent;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A merge that keeps the smaller id without touching the union-find, and records each call with the state it ran
    /// in, so tests can see which merges happened and whether they ran on the caller's state or a parallel clone.
    /// </summary>
    sealed class RecordingContainerMerge : ContainerMergeFunction
    {

        /// <summary>
        /// Gets the calls made, in completion order.
        /// </summary>
        public ConcurrentQueue<(uint Current, uint Incoming, ExecutionState State)> Calls { get; } = new();

        /// <inheritdoc />
        public override uint Merge(ExecutionState state, uint current, uint incoming)
        {
            Calls.Enqueue((current, incoming, state));
            return Math.Min(current, incoming);
        }

    }

}
