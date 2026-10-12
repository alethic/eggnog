using System;

namespace Eggnog
{

    /// <summary>
    /// The head action of a query (R1b) or a split matcher (R3): adds its arguments, one match, to the
    /// <see cref="ScopeRuntime.QueryMatches"/> of the scope the engine call runs in, and produces the unit value. For a
    /// query asked with <c>Any</c> (<see cref="ScopeRuntime.StopAtFirstQueryMatch"/>) it triggers the engine's early stop
    /// after the first match. It stages nothing, so a query writes nothing (D49).
    /// </summary>
    /// <remarks>
    /// No user code runs and nothing is read from a table (invariant Q); the buffer's <c>Add</c> is thread-safe, so rule
    /// tasks may call it concurrently. Outside a query (no buffer set) it collects nothing. Never fails: the bridge would
    /// otherwise call the panic function of its call.
    /// </remarks>
    internal sealed class MatchCollector : Relations.ExternalFunction
    {

        readonly EGraph graph;
        readonly uint unit;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph whose scope runtimes receive the matches.</param>
        /// <param name="width">The number of columns of a match.</param>
        /// <param name="unit">The unit value's cell, the call's result.</param>
        public MatchCollector(EGraph graph, int width, uint unit)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(width);
            this.graph = graph;
            this.unit = unit;
            Width = width;
        }

        /// <summary>
        /// Gets the number of columns of a match.
        /// </summary>
        public int Width { get; }

        /// <inheritdoc />
        public override bool TryInvoke(Relations.ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            var runtime = ScopeRuntime.Of(graph, state);
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
