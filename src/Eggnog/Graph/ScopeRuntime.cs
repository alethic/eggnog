namespace Eggnog
{

    /// <summary>
    /// Per-scope buffers that external functions write during engine calls: the tuples of a query or split matcher, and
    /// the first evaluation failure (D31). Thread-safe, because rule tasks may run in parallel.
    /// </summary>
    /// <remarks>
    /// R1: the root's runtime, held by the graph, serves every call (the root is the only scope). R2 gives every context
    /// its own through a per-graph context-local slot, reached from the execution state the engine passes.
    /// </remarks>
    internal sealed class ScopeRuntime
    {

        readonly object sync = new();
        EvaluationFailure? failure;

        /// <summary>
        /// Gets the runtime of the scope an engine call runs in. R1: always the root's.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="state">The execution state the engine passed to the external function.</param>
        /// <returns>The runtime.</returns>
        public static ScopeRuntime Of(EGraph graph, Relations.ExecutionState state) => graph.Root.Runtime;

        /// <summary>
        /// Gets or sets the scope this runtime belongs to.
        /// </summary>
        public Scope? Owner { get; set; }

        /// <summary>
        /// Records a failure unless one is recorded already: the first one wins. Thread-safe.
        /// </summary>
        /// <param name="failure">The failure.</param>
        /// <returns>Whether it was recorded.</returns>
        public bool TryRecord(EvaluationFailure failure)
        {
            lock (sync)
            {
                if (this.failure is not null)
                    return false;

                this.failure = failure;
                return true;
            }
        }

        /// <summary>
        /// Takes the recorded failure, if any, and clears it. Thread-safe.
        /// </summary>
        /// <returns>The failure, or <c>null</c>.</returns>
        public EvaluationFailure? TakeFailure()
        {
            lock (sync)
            {
                var taken = failure;
                failure = null;
                return taken;
            }
        }

        /// <summary>
        /// Gets or sets the buffer a query or split matcher's collector fills during one engine call (R1b, R3).
        /// </summary>
        public TupleBuffer? QueryMatches { get; set; }

        /// <summary>
        /// Gets or sets whether the collector stops the engine after the first match (<c>Any</c>, R1b).
        /// </summary>
        public bool StopAtFirstQueryMatch { get; set; }

    }

}
