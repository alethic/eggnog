using System;

namespace Eggnog
{

    /// <summary>
    /// How far a graph has used what it never reclaims: its interned values, its ids and its epochs. Every scope of the
    /// graph draws on them, and a disposed scope gives none of them back. <see cref="EGraph.Usage"/> takes the snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ids and epochs are 32-bit counters shared by every scope. A child scope that writes or runs reserves ids in
    /// blocks (16 for its first, so a query scope that makes one term reserves 16), and every write batch, run iteration,
    /// rebuild pass and query read (<see cref="Scope.Any"/>, <see cref="Scope.Count"/>, <see cref="Scope.ForEachMatch"/>)
    /// of any scope takes an epoch, and so does a read of a writable scope that must catch up first. A case split's
    /// cases take epochs in proportion to their number, so a workload that splits usually runs out of epochs before
    /// ids. When either runs out, the call that needs one throws <see cref="OverflowException"/>: the scope is not
    /// faulted, but every later call that needs an id or an epoch throws the same way, and once the epochs are out the
    /// graph creates no more children. Interned values are bounded too, at about 2.1 billion per type, but memory runs
    /// out long before that.
    /// </para>
    /// <para>
    /// A long-lived host retires a graph before it runs out: it builds a new graph from its definitions, moves new
    /// work to it, and lets the old one go once its last scope is disposed. <see cref="FractionUsed"/> is the measure
    /// to watch for that, beside <see cref="InternedValues"/> for memory.
    /// </para>
    /// </remarks>
    /// <param name="InternedValues">The values interned in every pool of the graph, as
    /// <see cref="ValueStore.InternedCount"/> reports them; pools never shrink.</param>
    /// <param name="IdsReserved">The ids handed out or reserved so far, by every scope, live or disposed; at most
    /// <paramref name="IdLimit"/>.</param>
    /// <param name="IdLimit">The number of ids the graph can hand out.</param>
    /// <param name="Epochs">The epochs used so far, by every scope, live or disposed; at most
    /// <paramref name="EpochLimit"/>.</param>
    /// <param name="EpochLimit">The number of epochs the graph can hand out.</param>
    public readonly record struct GraphUsage(long InternedValues, long IdsReserved, long IdLimit, long Epochs, long EpochLimit)
    {

        /// <summary>
        /// Gets the largest fraction of a limit used: the larger of <see cref="IdsReserved"/> over
        /// <see cref="IdLimit"/> and <see cref="Epochs"/> over <see cref="EpochLimit"/>, from 0 to 1. At 1, the graph
        /// has run out of ids or epochs. A limit of zero (a default value) counts as unused.
        /// </summary>
        public double FractionUsed => Math.Max(Fraction(IdsReserved, IdLimit), Fraction(Epochs, EpochLimit));

        /// <summary>
        /// Gets <paramref name="used"/> over <paramref name="limit"/>, or 0 for a limit that is not positive.
        /// </summary>
        static double Fraction(long used, long limit) => limit > 0 ? (double)used / limit : 0;

    }

}
