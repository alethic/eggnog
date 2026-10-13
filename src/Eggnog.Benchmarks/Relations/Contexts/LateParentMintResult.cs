using System.Collections.Generic;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The counters of one late-parent-mint workload (<see cref="LateParentMintBenchmarks"/>, colored-contexts spec §17
    /// M10.4, L7, G2, §4.2).
    /// </summary>
    internal sealed class LateParentMintResult
    {

        /// <summary>
        /// Gets the number of cases.
        /// </summary>
        public int Cases { get; init; }

        /// <summary>
        /// Gets the terms each case built (its <c>num</c> leaves; it also built one fewer <c>add</c> terms).
        /// </summary>
        public int Terms { get; init; }

        /// <summary>
        /// Gets the lift rounds that wrote into the parent.
        /// </summary>
        public int Rounds { get; init; }

        /// <summary>
        /// Gets the terms the lift minted in the parent.
        /// </summary>
        public int TermsLifted { get; init; }

        /// <summary>
        /// Gets the cases' own unions whose loser was a parent id beaten by a local id (G2), summed over the cases.
        /// </summary>
        public long ParentIdLosers { get; init; }

        /// <summary>
        /// Gets the ancestor rows the cases copied in each lift round, summed over the cases.
        /// </summary>
        public IReadOnlyList<long> CopiesPerRound { get; init; } = [];

        /// <summary>
        /// Gets the ids the root minted itself (its own ids and the lifted terms).
        /// </summary>
        public long RootMinted { get; init; }

        /// <summary>
        /// Gets the ids the cases reserved in blocks (§4.2), whether they minted them or not.
        /// </summary>
        public long CaseReserved { get; init; }

        /// <summary>
        /// Gets the root union-find's id array length before the lift.
        /// </summary>
        public int RootIdArrayBefore { get; init; }

        /// <summary>
        /// Gets the root union-find's id array length after the lift: it grows to the largest id the root unions.
        /// </summary>
        public int RootIdArrayAfter { get; init; }

        /// <inheritdoc />
        public override string ToString() => $"cases={Cases} terms={Terms} rounds={Rounds} lifted={TermsLifted} parentIdLosers={ParentIdLosers} copiesPerRound=[{string.Join(", ", CopiesPerRound)}] rootMinted={RootMinted} caseReserved={CaseReserved} rootIdArray={RootIdArrayBefore}->{RootIdArrayAfter}";

    }

}
