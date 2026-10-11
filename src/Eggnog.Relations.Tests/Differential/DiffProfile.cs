namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The knobs of the program generator (<see cref="DiffGenerator"/>) and the oracle's budget.
    /// </summary>
    /// <param name="Name">The profile's name, printed with failing programs.</param>
    /// <param name="MinTables">The fewest sorted-writes tables.</param>
    /// <param name="MaxTables">The most sorted-writes tables.</param>
    /// <param name="UnionFindChance">The probability that the program has a union-find.</param>
    /// <param name="MinDomain">The smallest value domain.</param>
    /// <param name="MaxDomain">The largest value domain.</param>
    /// <param name="MaxRowsPerLoad">The most rows one load step stages.</param>
    /// <param name="MinSteps">The fewest steps.</param>
    /// <param name="MaxSteps">The most steps.</param>
    /// <param name="MinAtoms">The fewest atoms per query.</param>
    /// <param name="MaxAtoms">The most atoms per query.</param>
    /// <param name="MaxQueriesPerRun">The most queries one run step holds.</param>
    /// <param name="MaxIterations">The most iterations of one run step.</param>
    /// <param name="ConfigsPerQuery">How many of the six strategy and decomposition combinations each query of a run
    /// step is planned under (all six, or a random subset).</param>
    /// <param name="ReferenceBudget">The most row visits the oracle may spend on one query.</param>
    /// <param name="MaxMatches">The most distinct matches a query may have.</param>
    /// <param name="MaxDataArity">The most non-timestamp columns a table may have.</param>
    /// <param name="PlantChance">The probability that a step running queries is preceded by loads of rows that satisfy
    /// them under a random assignment of their variables, so that even long queries match.</param>
    /// <param name="MinRowsPerLoad">The fewest rows one load step stages.</param>
    /// <param name="MaxUnionPairs">The most pairs one union step stages.</param>
    /// <param name="ProjectChance">The probability that a query projects its matches onto a random subset of its bound
    /// variables (<see cref="DiffQuery.Projection"/>), so that the action leaves some variables unread.</param>
    /// <param name="DenseChance">The probability that a program gets the tables of the lost-constraint shape
    /// (<see cref="DiffProgram.DenseTables"/>); half of its run-step and cached-loop queries then have that shape
    /// (<see cref="DiffShape.Dense"/>), which is always projected.</param>
    sealed record DiffProfile(
        string Name,
        int MinTables,
        int MaxTables,
        double UnionFindChance,
        int MinDomain,
        int MaxDomain,
        int MaxRowsPerLoad,
        int MinSteps,
        int MaxSteps,
        int MinAtoms,
        int MaxAtoms,
        int MaxQueriesPerRun,
        int MaxIterations,
        int ConfigsPerQuery,
        long ReferenceBudget,
        int MaxMatches,
        int MaxDataArity = 4,
        double PlantChance = 0.3,
        int MinRowsPerLoad = 2,
        int MaxUnionPairs = 3,
        double ProjectChance = 0.25,
        double DenseChance = 0.0)
    {

        /// <summary>
        /// Small programs: two to four tables over a domain of two to five values, queries of two to six atoms
        /// planned under every strategy with decomposition on and off. Thousands run in seconds.
        /// </summary>
        public static DiffProfile Small { get; } = new("small", 2, 4, 0.6, 2, 5, 10, 8, 16, 2, 6, 2, 2, 6, 400_000, 3_000, DenseChance: 0.1);

        /// <summary>
        /// Larger tables (up to 120 rows per load over up to 40 values) and queries of two to four atoms, so candidate
        /// sets cross the executor's size thresholds (sparse column indexes, eager refinement, cached indexes for dense
        /// ranges, several action batches) and tables compact.
        /// </summary>
        public static DiffProfile Larger { get; } = new("larger", 2, 4, 0.6, 8, 40, 120, 6, 12, 2, 4, 2, 2, 3, 2_000_000, 20_000, DenseChance: 0.15);

        /// <summary>
        /// Bulk tables (up to 900 rows per load over up to 300 values) and queries of two or three atoms: column indexes
        /// past their bulk-sort threshold (512 rows), parallel morsels (256 frames) and many action batches (128 lanes).
        /// </summary>
        public static DiffProfile Bulk { get; } = new("bulk", 2, 3, 0.5, 60, 300, 900, 5, 9, 2, 3, 1, 2, 2, 20_000_000, 60_000, DenseChance: 0.6);

        /// <summary>
        /// Wide tables (up to eight columns) and long queries (ten to twenty atoms) with planted matches over a wide
        /// domain, so that queries hold more than 64 variables and plans have many stages: variable sets past one
        /// machine word, deep tree decompositions.
        /// </summary>
        public static DiffProfile Wide { get; } = new("wide", 1, 3, 0.3, 30, 60, 14, 5, 8, 10, 20, 1, 1, 3, 5_000_000, 20_000, 8, 1.0);

        /// <summary>
        /// Huge tables (up to 24000 rows per load) with a union-find and queries of one or two atoms: past the parallel
        /// thresholds of inserts (20000 rows), rebuilds and database-level merges (10000), and the incremental rebuild
        /// path (more than 10000 rows and few new unions).
        /// </summary>
        public static DiffProfile Huge { get; } = new("huge", 1, 2, 1.0, 30_000, 30_000, 24_000, 6, 9, 1, 2, 1, 1, 2, 20_000_000, 100_000, 3, 0.0, 15_000, 400);

        /// <summary>
        /// Gets the profile named <paramref name="name"/>.
        /// </summary>
        public static DiffProfile ByName(string name) => name switch
        {
            "larger" => Larger,
            "bulk" => Bulk,
            "wide" => Wide,
            "huge" => Huge,
            _ => Small,
        };

    }

}
