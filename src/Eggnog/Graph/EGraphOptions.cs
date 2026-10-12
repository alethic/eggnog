namespace Eggnog
{

    /// <summary>
    /// How an <see cref="EGraph"/> runs.
    /// </summary>
    public sealed class EGraphOptions
    {

        /// <summary>
        /// Gets the default options: serial and deterministic.
        /// </summary>
        public static EGraphOptions Default { get; } = new();

        /// <summary>
        /// Gets the degree of parallelism of rule tasks and rebuilds at the root (D46). 1, the default: serial and
        /// deterministic, extraction ties included (decision 4). More: the facts are equal modulo renaming of classes, but
        /// fresh ids, and so extraction ties, may differ between runs. Must be at least 1
        /// (<see cref="System.ArgumentOutOfRangeException"/> at graph construction).
        /// </summary>
        public int DegreeOfParallelism { get; init; } = 1;

        /// <summary>
        /// Gets whether each rule's query plan is recorded in <see cref="RuleStatistics.Plan"/>: the graph's engine then
        /// reports at its plan level, set at graph construction (D41). Off by default, because rendering a plan costs a
        /// little on every iteration.
        /// </summary>
        public bool RecordPlans { get; init; }

    }

}
