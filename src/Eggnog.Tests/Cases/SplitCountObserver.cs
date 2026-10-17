using System.Collections.Generic;

namespace Eggnog.Tests.Cases
{

    /// <summary>
    /// A run observer that counts, per split step of a writable scope, the rows its cases examined: in catch-ups with
    /// the scope (<see cref="Relations.LayerStatistics.CatchUpRowsExamined"/>), and in their rule runs' scans and
    /// dynamic indexes (<see cref="Relations.ExecutorCounters"/>, which needs <c>EGGNOG_COUNTERS</c>).
    /// </summary>
    /// <remarks>
    /// The counters of a case are cumulative and its own (one storage context and one database per case), and a case
    /// lives until the run ends, so the observer keeps the last reading of every case it has seen alive, and charges to
    /// a split step what the cases' totals grew by since the previous one ended: all of a case's work happens inside
    /// the split steps of its scope (the catch-up before each in-case run, the in-case runs, and the lifts between). It
    /// reads the cases at every step, so a case disposed early (a vacuous one) keeps its last reading.
    /// </remarks>
    public sealed class SplitCountObserver
    {

        readonly Scope top;
        readonly Dictionary<Scope, (long CatchUp, long Scanned, long Indexed)> readings = new(ReferenceEqualityComparer.Instance);
        readonly List<SplitStepCounts> steps = new();
        (long CatchUp, long Scanned, long Indexed) charged;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="top">The scope the run splits, whose children are the cases.</param>
        public SplitCountObserver(Scope top)
        {
            this.top = top;
        }

        /// <summary>
        /// Gets the counts of every split step of <c>top</c> that opened or ran cases, in order.
        /// </summary>
        public IReadOnlyList<SplitStepCounts> Steps => steps;

        /// <summary>
        /// Observes a step: reads every live case, and closes the counts of a split step of <c>top</c>.
        /// </summary>
        /// <param name="step">The step.</param>
        public void Observe(RunStep step)
        {
            var cases = top.Children;
            foreach (var @case in cases)
                if (@case.IsCase)
                    Read(@case);

            if (step.Kind != RunStepKind.SplitStep || ReferenceEquals(step.Scope, top) == false)
                return;

            var total = Total();
            var counts = new SplitStepCounts(step.Index, cases.Count, total.CatchUp - charged.CatchUp, total.Scanned - charged.Scanned, total.Indexed - charged.Indexed);
            charged = total;
            if (counts.Cases > 0 || counts.Total > 0)
                steps.Add(counts);
        }

        /// <summary>
        /// Takes the current reading of a live case.
        /// </summary>
        void Read(Scope @case)
        {
            var context = @case.Engine.Context.Context;
            if (context.IsDisposed)
                return;

            var catchUp = context.Tables?.Statistics.CatchUpRowsExamined ?? 0;
            long scanned = 0, indexed = 0;
            if (context.Database.ExecutorCounts is { } counts)
            {
                scanned = counts.OwnRowsScanned + counts.AncestorRowsScanned + counts.AncestorRefinedRowsScanned;
                indexed = counts.OwnDynamicIndexRows + counts.AncestorDynamicIndexRows + counts.AncestorRefinedIndexRows;
            }

            readings[@case] = (catchUp, scanned, indexed);
        }

        /// <summary>
        /// Sums the last readings of every case seen: after the run, every row its cases examined.
        /// </summary>
        /// <returns>The rows examined in catch-ups, scans and indexes.</returns>
        public (long CatchUp, long Scanned, long Indexed) Total()
        {
            long catchUp = 0, scanned = 0, indexed = 0;
            foreach (var reading in readings.Values)
            {
                catchUp += reading.CatchUp;
                scanned += reading.Scanned;
                indexed += reading.Indexed;
            }

            return (catchUp, scanned, indexed);
        }

    }

}
