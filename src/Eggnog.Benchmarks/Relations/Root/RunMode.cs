namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Which of the engine's execution paths a root benchmark measures: the serial paths, or the parallel ones. Public
    /// because benchmark parameters are.
    /// </summary>
    /// <remarks>
    /// <see cref="RootParallelism"/> turns a mode into the <see cref="ParallelHeuristics"/> the database and its tables
    /// are built with, and into the database's choice of rule-set executor.
    /// </remarks>
    public enum RunMode
    {

        /// <summary>
        /// One thread: <see cref="ParallelHeuristics.Serial"/> for the database and its tables, so rule sets, merges,
        /// rebuilds and index refreshes take their serial paths. Deterministic (decision 4).
        /// </summary>
        Serial,

        /// <summary>
        /// <see cref="RootParallelism.ParallelDegree"/> threads for the database and its tables, with the parallel
        /// rule-set executor forced on, as the executor tests' <c>"Parallel4"</c> mode forces it: merges, rebuilds and
        /// index refreshes go parallel wherever their egglog size thresholds say so.
        /// </summary>
        Parallel,

    }

}
