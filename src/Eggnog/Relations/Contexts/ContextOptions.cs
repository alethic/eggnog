namespace Eggnog.Relations
{

    /// <summary>
    /// How a child context behaves: passed to <see cref="Context.CreateChild"/> (colored-contexts spec §3.3).
    /// Immutable once built.
    /// </summary>
    internal sealed class ContextOptions
    {

        /// <summary>
        /// The options of an ordinary child: contradiction is an ordinary fact, parallelism follows the C9 default,
        /// and watermarks may be inherited.
        /// </summary>
        public static ContextOptions Default { get; } = new();

        /// <summary>
        /// The options of a case of a split (rule-layer D11): a contradiction stops the context, which then answers
        /// only whether it is contradictory and is disposed as vacuous (L4).
        /// </summary>
        public static ContextOptions Case { get; } = new() { IsCase = true, StopOnContradiction = true };

        /// <summary>
        /// Gets whether merging a row into the store's designated contradiction table stops the context (§6.9, X11).
        /// Default <c>false</c>: for host runs a contradiction is an ordinary fact (D11).
        /// </summary>
        public bool StopOnContradiction { get; init; }

        /// <summary>
        /// Gets the parallelism of the context's database, or <c>null</c> for the C9 default (§10.9): a child of a
        /// frozen context runs serially, a child of a context that is not frozen inherits its parent's heuristics
        /// (the two never run concurrently).
        /// </summary>
        public ParallelHeuristics? Parallelism { get; init; }

        /// <summary>
        /// Gets whether the context is a case of a split, for the lift and split drivers (§11).
        /// </summary>
        public bool IsCase { get; init; }

        /// <summary>
        /// Gets whether the bridge may seed this context's rule watermarks from its ancestors' (§10.2). Default
        /// <c>true</c>; per context, so tests can compare seeded and unseeded runs side by side (X17).
        /// </summary>
        public bool InheritWatermarks { get; init; } = true;

    }

}
