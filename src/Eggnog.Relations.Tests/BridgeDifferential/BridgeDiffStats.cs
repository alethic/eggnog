using System.Globalization;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What the bridge differential programs exercised, summed over programs: so the suite can check that its generator
    /// keeps reaching what it is meant to test, and the soak can report it. One instance per program while it runs, summed
    /// with <see cref="Add"/> (which locks the target), so programs can run in parallel.
    /// </summary>
    sealed class BridgeDiffStats
    {

        readonly object gate = new();

        /// <summary>Gets or sets the programs run.</summary>
        public long Programs { get; set; }

        /// <summary>Gets or sets the iterations run.</summary>
        public long Iterations { get; set; }

        /// <summary>Gets or sets the iterations that ran only some of the rules.</summary>
        public long PartialIterations { get; set; }

        /// <summary>Gets or sets the iterations that changed the e-graph.</summary>
        public long ChangedIterations { get; set; }

        /// <summary>Gets or sets the iterations whose rules made an effective union, so the bridge rebuilt.</summary>
        public long RebuildingIterations { get; set; }

        /// <summary>Gets or sets the programs whose rules saturated.</summary>
        public long Saturated { get; set; }

        /// <summary>Gets or sets the programs stopped at the row cap.</summary>
        public long Capped { get; set; }

        /// <summary>Gets or sets the programs stopped because an iteration exceeded the oracle's match budget.</summary>
        public long OverBudget { get; set; }

        /// <summary>Gets or sets the programs that panicked, in the same iteration in every evaluation.</summary>
        public long Panicked { get; set; }

        /// <summary>Gets or sets the ids the oracle's rules minted.</summary>
        public long Minted { get; set; }

        /// <summary>Gets or sets the matches the oracle enumerated.</summary>
        public long OracleMatches { get; set; }

        /// <summary>Gets or sets the matches the semi-naive bridge reported.</summary>
        public long SemiNaiveMatches { get; set; }

        /// <summary>Gets or sets the matches the naive bridge reported.</summary>
        public long NaiveMatches { get; set; }

        /// <summary>Gets or sets the classes that merged, at the end of the programs (ids minus classes).</summary>
        public long MergedIds { get; set; }

        /// <summary>Gets or sets the subsumed rows at the end of the programs.</summary>
        public long SubsumedRows { get; set; }

        /// <summary>Gets or sets the lattice rows at the end of the programs.</summary>
        public long LatticeRows { get; set; }

        /// <summary>Gets or sets the relation rows at the end of the programs.</summary>
        public long RelationRows { get; set; }

        /// <summary>Gets or sets the rows of functions without keys (egglog's globals) at the end of the programs.</summary>
        public long KeylessRows { get; set; }

        /// <summary>Gets or sets the rows of functions with more than three keys at the end of the programs.</summary>
        public long WideRows { get; set; }

        /// <summary>Gets or sets the programs with seeds between iterations.</summary>
        public long LateSeedPrograms { get; set; }

        /// <summary>
        /// Gets or sets the programs whose e-graphs ran with parallel settings: four threads and the parallel rule-set
        /// executor, which they take whatever the size of the database (<see cref="BridgeDiffBridge"/>). Table operations
        /// go parallel only above the engine's thresholds, which only bulk programs reach.
        /// </summary>
        public long ParallelPrograms { get; set; }

        /// <summary>
        /// Adds <paramref name="other"/>'s counts to these, under a lock.
        /// </summary>
        /// <param name="other">The counts of one or more programs.</param>
        public void Add(BridgeDiffStats other)
        {
            lock (gate)
            {
                Programs += other.Programs;
                Iterations += other.Iterations;
                PartialIterations += other.PartialIterations;
                ChangedIterations += other.ChangedIterations;
                RebuildingIterations += other.RebuildingIterations;
                Saturated += other.Saturated;
                Capped += other.Capped;
                OverBudget += other.OverBudget;
                Panicked += other.Panicked;
                Minted += other.Minted;
                OracleMatches += other.OracleMatches;
                SemiNaiveMatches += other.SemiNaiveMatches;
                NaiveMatches += other.NaiveMatches;
                MergedIds += other.MergedIds;
                SubsumedRows += other.SubsumedRows;
                LatticeRows += other.LatticeRows;
                RelationRows += other.RelationRows;
                KeylessRows += other.KeylessRows;
                WideRows += other.WideRows;
                LateSeedPrograms += other.LateSeedPrograms;
                ParallelPrograms += other.ParallelPrograms;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder();
            void Item(string name, long value) => b.Append(name).Append('=').Append(value.ToString(CultureInfo.InvariantCulture)).Append(' ');

            Item("programs", Programs);
            Item("iterations", Iterations);
            Item("partial", PartialIterations);
            Item("changed", ChangedIterations);
            Item("rebuilding", RebuildingIterations);
            Item("saturated", Saturated);
            Item("capped", Capped);
            Item("over-budget", OverBudget);
            Item("panicked", Panicked);
            Item("minted", Minted);
            Item("oracle-matches", OracleMatches);
            Item("semi-naive-matches", SemiNaiveMatches);
            Item("naive-matches", NaiveMatches);
            Item("merged-ids", MergedIds);
            Item("subsumed-rows", SubsumedRows);
            Item("lattice-rows", LatticeRows);
            Item("relation-rows", RelationRows);
            Item("keyless-rows", KeylessRows);
            Item("wide-rows", WideRows);
            Item("late-seeds", LateSeedPrograms);
            Item("parallel", ParallelPrograms);
            return b.ToString().TrimEnd();
        }

    }

}
