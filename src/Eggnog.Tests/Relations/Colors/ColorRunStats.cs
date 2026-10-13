using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What one or more <see cref="ColorRunner"/> programs exercised, so that the differentials can assert they are not
    /// vacuous.
    /// </summary>
    sealed class ColorRunStats
    {

        /// <summary>
        /// The commands run (not skipped).
        /// </summary>
        public long Commands;

        /// <summary>
        /// The rule iterations run in children.
        /// </summary>
        public long ChildIterations;

        /// <summary>
        /// The rule iterations run at the root.
        /// </summary>
        public long RootIterations;

        /// <summary>
        /// The iterations that changed a view.
        /// </summary>
        public long ChangingIterations;

        /// <summary>
        /// The matches the reference fired.
        /// </summary>
        public long ReferenceMatches;

        /// <summary>
        /// The reference's rule runs in children that used an inherited watermark.
        /// </summary>
        public long InheritedRuns;

        /// <summary>
        /// The engine's matches, with inheritance.
        /// </summary>
        public long SeededMatches;

        /// <summary>
        /// The unseeded engine's matches.
        /// </summary>
        public long UnseededMatches;

        /// <summary>
        /// The deletes and clears applied in children.
        /// </summary>
        public long ChildRetractions;

        /// <summary>
        /// The deletes and clears applied at the root.
        /// </summary>
        public long RootRetractions;

        /// <summary>
        /// The deletes and clears the engine refused, as §6.7 says.
        /// </summary>
        public long Refusals;

        /// <summary>
        /// The contexts frozen, the root's included.
        /// </summary>
        public long Freezes;

        /// <summary>
        /// The contexts disposed.
        /// </summary>
        public long Disposals;

        /// <summary>
        /// The fresh-root copies compared.
        /// </summary>
        public long Copies;

        /// <summary>
        /// The conjunctive queries compared.
        /// </summary>
        public long Queries;

        /// <summary>
        /// The rows compared at checkpoints.
        /// </summary>
        public long RowsCompared;

        /// <summary>
        /// Adds <paramref name="other"/>'s counts.
        /// </summary>
        public void Add(ColorRunStats other)
        {
            Commands += other.Commands;
            ChildIterations += other.ChildIterations;
            RootIterations += other.RootIterations;
            ChangingIterations += other.ChangingIterations;
            ReferenceMatches += other.ReferenceMatches;
            InheritedRuns += other.InheritedRuns;
            SeededMatches += other.SeededMatches;
            UnseededMatches += other.UnseededMatches;
            ChildRetractions += other.ChildRetractions;
            RootRetractions += other.RootRetractions;
            Refusals += other.Refusals;
            Freezes += other.Freezes;
            Disposals += other.Disposals;
            Copies += other.Copies;
            Queries += other.Queries;
            RowsCompared += other.RowsCompared;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append($"{Commands} commands, {RootIterations} root and {ChildIterations} child iterations ({ChangingIterations} changing), ");
            b.Append($"{ReferenceMatches} reference matches, {InheritedRuns} inherited runs, matches {SeededMatches} seeded / {UnseededMatches} unseeded, ");
            b.Append($"{ChildRetractions} child and {RootRetractions} root retractions, {Refusals} refusals, {Freezes} freezes, {Disposals} disposals, ");
            b.Append($"{Copies} copies, {Queries} queries, {RowsCompared} rows compared");
            return b.ToString();
        }

    }

}
