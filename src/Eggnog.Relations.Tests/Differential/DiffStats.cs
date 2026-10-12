using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a batch of differential programs exercised, so that the tests can insist the generator still reaches the
    /// paths it is meant to (nonempty matches, decomposed plans, rebuild cascades, compaction) and report it.
    /// </summary>
    sealed class DiffStats
    {

        /// <summary>
        /// Gets or sets the number of programs run.
        /// </summary>
        public int Programs { get; set; }

        /// <summary>
        /// Gets or sets the number of steps run.
        /// </summary>
        public int Steps { get; set; }

        /// <summary>
        /// Gets or sets the number of rules run and checked.
        /// </summary>
        public int Rules { get; set; }

        /// <summary>
        /// Gets or sets the number of those rules that had at least one match.
        /// </summary>
        public int RulesWithMatches { get; set; }

        /// <summary>
        /// Gets or sets the number of rules with a decomposed plan.
        /// </summary>
        public int DecomposedRules { get; set; }

        /// <summary>
        /// Gets or sets the number of rules whose decomposed plan checks a materialization through a
        /// <see cref="MatScanMode.Lookup"/> stage (see <see cref="DiffRunner.HasMaterializationLookup"/>).
        /// </summary>
        public int LookupRules { get; set; }

        /// <summary>
        /// Gets or sets the number of rules built from a query that projects some bound variable away.
        /// </summary>
        public int ProjectedRules { get; set; }

        /// <summary>
        /// Gets or sets the number of rules added from a cached plan.
        /// </summary>
        public int CachedRules { get; set; }

        /// <summary>
        /// Gets or sets the number of rules whose multiplicity was checked.
        /// </summary>
        public int MultiplicityChecks { get; set; }

        /// <summary>
        /// Gets or sets the number of distinct matches compared.
        /// </summary>
        public long Matches { get; set; }

        /// <summary>
        /// Gets or sets the number of queries dropped because the oracle's budget ran out.
        /// </summary>
        public int SkippedQueries { get; set; }

        /// <summary>
        /// Gets or sets the number of semi-naive variants checked.
        /// </summary>
        public int SemiNaiveVariants { get; set; }

        /// <summary>
        /// Gets or sets the number of semi-naive variants skipped by the engine because a header was empty.
        /// </summary>
        public int SkippedVariants { get; set; }

        /// <summary>
        /// Gets or sets the number of rebuild passes run.
        /// </summary>
        public int RebuildPasses { get; set; }

        /// <summary>
        /// Gets or sets the number of rebuild passes that changed something.
        /// </summary>
        public int ChangingRebuildPasses { get; set; }

        /// <summary>
        /// Gets or sets the number of table generations started (compactions and clears).
        /// </summary>
        public int Generations { get; set; }

        /// <summary>
        /// Gets or sets the number of rows compared.
        /// </summary>
        public long RowsCompared { get; set; }

        /// <summary>
        /// Gets or sets the most variables a checked rule's query had.
        /// </summary>
        public int MaxVariables { get; set; }

        /// <summary>
        /// Gets or sets the number of semi-naive variants run over a layered copy (<see cref="DiffLayeredSemiNaiveStep"/>).
        /// </summary>
        public int LayeredVariants { get; set; }

        /// <summary>
        /// Gets or sets the number of those that had matches.
        /// </summary>
        public int LayeredVariantsWithMatches { get; set; }

        /// <summary>
        /// Gets or sets the number of rows the layered copies hid (rows of lower layers a higher one overrides).
        /// </summary>
        public long LayeredHiddenRows { get; set; }

        /// <summary>
        /// Adds <paramref name="other"/> into this.
        /// </summary>
        public void Add(DiffStats other)
        {
            Programs += other.Programs;
            Steps += other.Steps;
            Rules += other.Rules;
            RulesWithMatches += other.RulesWithMatches;
            DecomposedRules += other.DecomposedRules;
            LookupRules += other.LookupRules;
            ProjectedRules += other.ProjectedRules;
            CachedRules += other.CachedRules;
            MultiplicityChecks += other.MultiplicityChecks;
            Matches += other.Matches;
            SkippedQueries += other.SkippedQueries;
            SemiNaiveVariants += other.SemiNaiveVariants;
            SkippedVariants += other.SkippedVariants;
            RebuildPasses += other.RebuildPasses;
            ChangingRebuildPasses += other.ChangingRebuildPasses;
            Generations += other.Generations;
            RowsCompared += other.RowsCompared;
            MaxVariables = System.Math.Max(MaxVariables, other.MaxVariables);
            LayeredVariants += other.LayeredVariants;
            LayeredVariantsWithMatches += other.LayeredVariantsWithMatches;
            LayeredHiddenRows += other.LayeredHiddenRows;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append(Programs).Append(" programs, ").Append(Steps).Append(" steps, ");
            b.Append(Rules).Append(" rules (").Append(RulesWithMatches).Append(" matching, ").Append(DecomposedRules).Append(" decomposed, ").Append(LookupRules).Append(" with materialization lookups, ").Append(ProjectedRules).Append(" projected, ").Append(CachedRules).Append(" cached, ").Append(MultiplicityChecks).Append(" multiplicity-checked), ");
            b.Append(Matches).Append(" matches, ").Append(SkippedQueries).Append(" queries over budget, ");
            b.Append(SemiNaiveVariants).Append(" semi-naive variants (").Append(SkippedVariants).Append(" skipped as empty), ");
            b.Append(RebuildPasses).Append(" rebuild passes (").Append(ChangingRebuildPasses).Append(" changing), ");
            b.Append(Generations).Append(" table generations, ").Append(RowsCompared).Append(" rows compared, ");
            b.Append("at most ").Append(MaxVariables).Append(" variables per rule");
            if (LayeredVariants > 0)
                b.Append(", ").Append(LayeredVariants).Append(" layered variants (").Append(LayeredVariantsWithMatches).Append(" matching, ").Append(LayeredHiddenRows).Append(" hidden rows)");

            return b.ToString();
        }

    }

}
