namespace Eggnog.Relations
{

    /// <summary>
    /// How a child context behaves: passed to <see cref="Context.CreateChild"/>. Immutable once built.
    /// </summary>
    internal sealed class ContextOptions
    {

        /// <summary>
        /// The options of an ordinary child: parallelism follows the default described on <see cref="Parallelism"/>,
        /// and watermarks may be inherited.
        /// </summary>
        public static ContextOptions Default { get; } = new();

        /// <summary>
        /// Gets the parallelism of the context's database, or <c>null</c> for the default: a child of a frozen context
        /// runs serially, a child of a context that is not frozen inherits its parent's heuristics (the two never run
        /// concurrently).
        /// </summary>
        public ParallelHeuristics? Parallelism { get; init; }

        /// <summary>
        /// Gets whether the bridge may seed this context's rule watermarks from its ancestors'. Default <c>true</c>;
        /// per context rather than global, so tests can compare seeded and unseeded runs side by side.
        /// </summary>
        public bool InheritWatermarks { get; init; } = true;

    }

}
