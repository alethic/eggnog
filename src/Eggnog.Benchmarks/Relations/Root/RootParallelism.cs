using System;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Turns a <see cref="RunMode"/> into the parallelism settings of the root benchmark set: the
    /// <see cref="ParallelHeuristics"/> databases, tables and indexes are built with, and a database configured to run
    /// rule sets on the mode's executor.
    /// </summary>
    /// <remarks>
    /// The parallel degree is fixed per machine (<see cref="ParallelDegree"/>), so every parallel case of the set runs
    /// on the same number of threads and a baseline taken on one machine compares with later runs on it.
    /// </remarks>
    internal static class RootParallelism
    {

        /// <summary>
        /// The heuristics of <see cref="RunMode.Parallel"/>, created once.
        /// </summary>
        static readonly ParallelHeuristics parallel = new(ParallelDegree);

        /// <summary>
        /// Gets the number of threads <see cref="RunMode.Parallel"/> runs on: the machine's logical cores, at least two
        /// (so the parallel paths run even on one core) and at most eight (the largest thread count the other benchmark
        /// classes use).
        /// </summary>
        public static int ParallelDegree => Math.Clamp(Environment.ProcessorCount, 2, 8);

        /// <summary>
        /// Gets the parallelism settings of <paramref name="mode"/>.
        /// </summary>
        /// <param name="mode">The mode.</param>
        /// <returns><see cref="ParallelHeuristics.Serial"/>, or one shared instance on <see cref="ParallelDegree"/>
        /// threads.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined mode.</exception>
        public static ParallelHeuristics For(RunMode mode) => mode switch
        {
            RunMode.Serial => ParallelHeuristics.Serial,
            RunMode.Parallel => parallel,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown run mode."),
        };

        /// <summary>
        /// Creates an empty database for <paramref name="mode"/>: built with <see cref="For"/>'s heuristics, and, in
        /// <see cref="RunMode.Parallel"/>, running every rule set on the parallel executor whatever the database's size
        /// (<see cref="Database.ParallelRuleSetsOverride"/>), as the executor tests' parallel modes do.
        /// </summary>
        /// <param name="mode">The mode.</param>
        /// <returns>The database.</returns>
        public static Database CreateDatabase(RunMode mode)
        {
            var database = new Database(For(mode));
            database.ParallelRuleSetsOverride = mode == RunMode.Parallel ? true : null;
            return database;
        }

    }

}
