namespace Eggnog
{

    /// <summary>
    /// How a child scope runs: passed to <see cref="Scope.CreateChild"/>.
    /// </summary>
    public sealed class ScopeOptions
    {

        /// <summary>
        /// Gets the default options: <see cref="DegreeOfParallelism"/> unset, so a child of a frozen scope runs
        /// serially and a child of a writable one with its parent's parallelism.
        /// </summary>
        public static ScopeOptions Default { get; } = new();

        /// <summary>
        /// Gets the scope's degree of parallelism (how many threads the engine may use for it); <c>null</c>, the
        /// default, for serial in a child of a frozen scope and the parent's in a child of a writable one. Must be at
        /// least 1 (<see cref="System.ArgumentOutOfRangeException"/> from <see cref="Scope.CreateChild"/>). More than
        /// 1: the scope's facts are equal modulo renaming of classes, but fresh ids, and so extraction ties, may differ
        /// between runs.
        /// </summary>
        public int? DegreeOfParallelism { get; init; }

    }

}
