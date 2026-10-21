using System;

namespace Eggnog
{

    /// <summary>
    /// The head action of a query, a split matcher or a scheduler's query: adds its arguments, one match, to the
    /// <see cref="ScopeRuntime.QueryMatches"/> of the scope the engine call runs in, or for a scheduler's query to its
    /// rule's buffer in <see cref="ScopeRuntime.SchedulerMatches"/>, and produces the unit value. For a query asked with
    /// <c>Any</c> (<see cref="ScopeRuntime.StopAtFirstQueryMatch"/>) it triggers the engine's early stop after the first
    /// match. It stages nothing, so a query writes nothing.
    /// </summary>
    /// <remarks>
    /// No user code runs and nothing is read from a table, so the action depends only on its match; the buffer's
    /// <c>Add</c> is thread-safe, so rule tasks may call it concurrently. Outside a query (no buffer set) it collects
    /// nothing. Never fails: the bridge would otherwise call the panic function of its call.
    /// </remarks>
    internal sealed class MatchCollector : Relations.ExternalFunction
    {

        readonly EGraph graph;
        readonly uint unit;
        readonly int slot;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph whose scope runtimes receive the matches.</param>
        /// <param name="width">The number of columns of a match.</param>
        /// <param name="unit">The unit value's cell, the call's result.</param>
        /// <param name="slot">A scheduler's query's rule slot (<see cref="ScopeRuntime.SchedulerMatches"/>); -1 for a
        /// query or a split matcher.</param>
        public MatchCollector(EGraph graph, int width, uint unit, int slot = -1)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(width);
            this.graph = graph;
            this.unit = unit;
            this.slot = slot;
            Width = width;
        }

        /// <summary>
        /// Gets the number of columns of a match.
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// Gets whether the rule's actions read beyond its match: for a scheduler's query, which then never seeds its
        /// watermark from an ancestor's. The matches its ancestor's scheduler held back are not in the child's buffer, so
        /// a child continues from the progress its scheduler state carries instead (<see cref="Schedule.RunWith"/>).
        /// </summary>
        public override bool ReadsTables => slot >= 0;

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            var runtime = ScopeRuntime.Of(graph, state);
            if (slot >= 0)
            {
                if (runtime.SchedulerMatches is { } buffers && buffers.TryGetValue(slot, out var buffer) && args.Length == Width)
                    buffer.Add(args);

                result = unit;
                return true;
            }

            if (runtime.QueryMatches is { } matches && args.Length == Width)
            {
                matches.Add(args);
                if (runtime.StopAtFirstQueryMatch)
                    state.TriggerEarlyStop();
            }

            result = unit;
            return true;
        }

    }

}
