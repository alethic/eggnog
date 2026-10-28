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
        /// Gets the degree of parallelism of rule tasks and rebuilds at the root. 1, the default: serial and
        /// deterministic, extraction ties included. More: the facts are equal modulo renaming of classes, but
        /// fresh ids, and so extraction ties, may differ between runs. 0: every processor of the machine
        /// (<see cref="System.Environment.ProcessorCount"/>), as egglog's <c>-j 0</c> when <c>RAYON_NUM_THREADS</c> is
        /// unset. Must not be negative (<see cref="System.ArgumentOutOfRangeException"/> at graph construction).
        /// </summary>
        public int DegreeOfParallelism { get; init; } = 1;

        /// <summary>
        /// Gets whether each rule's query plan is recorded in <see cref="RuleStatistics.Plan"/>: the graph's engine then
        /// reports at its plan level, set at graph construction. Off by default, because rendering a plan costs a
        /// little on every iteration.
        /// </summary>
        public bool RecordPlans { get; init; }

    }

}
