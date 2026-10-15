using System.Globalization;

namespace Eggnog.Tests.Cases
{

    /// <summary>
    /// The rows the cases of one split step examined, by kind: in catch-ups with their writable parent, in the scans of
    /// their rule runs, and in the dynamic indexes those runs built. <see cref="SplitCountObserver"/> takes them from
    /// the cases' engine counters.
    /// </summary>
    /// <param name="Step">The split step's index among the run's steps.</param>
    /// <param name="Cases">The cases alive when the step ended.</param>
    /// <param name="CatchUp">The ancestor rows the cases' catch-ups examined.</param>
    /// <param name="Scanned">The rows the cases' rule runs scanned: own, ancestor and refined ancestor rows.</param>
    /// <param name="Indexed">The rows the cases' rule runs put into dynamic indexes: own, ancestor and refined ancestor
    /// rows.</param>
    public sealed record SplitStepCounts(int Step, int Cases, long CatchUp, long Scanned, long Indexed)
    {

        /// <summary>
        /// Gets every row the step's cases examined.
        /// </summary>
        public long Total => CatchUp + Scanned + Indexed;

        /// <summary>
        /// Gets catch-up's share of <see cref="Total"/>, from 0 to 1; 0 when the step examined nothing.
        /// </summary>
        public double CatchUpShare => Total == 0 ? 0 : (double)CatchUp / Total;

        /// <inheritdoc />
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"step {Step}: {Cases} cases, catch-up {CatchUp}, scanned {Scanned}, indexed {Indexed}, catch-up share {CatchUpShare:P1}");

    }

}
