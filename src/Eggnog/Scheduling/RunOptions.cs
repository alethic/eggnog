using System;

namespace Eggnog
{

    /// <summary>
    /// How a <see cref="Scope.Run(Schedule, RunOptions, System.Threading.CancellationToken)"/> runs. Limits are per
    /// run, and checked before every iteration (the first included), a step of <see cref="Schedule.RunWith"/> counting
    /// as one; a run overshoots a limit or a cancellation by at most one iteration, which cannot be interrupted. A
    /// limit is not an error: the run stops soundly, with fewer facts, and <see cref="RunReport.StopReason"/> says why.
    /// </summary>
    public sealed class RunOptions
    {

        /// <summary>
        /// Gets the default options.
        /// </summary>
        public static RunOptions Default { get; } = new();

        /// <summary>
        /// Gets the most iterations the run may make, a step of <see cref="Schedule.RunWith"/> counting as one; must be
        /// at least 0. The default, <see cref="int.MaxValue"/>, is the largest count the iteration counter holds and in
        /// effect sets no limit: as in egglog, a <c>Saturate</c> runs until nothing changes.
        /// </summary>
        public int IterationLimit { get; init; } = int.MaxValue;

        /// <summary>
        /// Gets the longest the run may take, measured from its start; <c>null</c>, the default, for no limit. Must not be
        /// negative. Checked before every iteration: once it has passed, the run stops with
        /// <see cref="RunStopReason.TimeLimit"/>, so it overshoots by at most one iteration.
        /// </summary>
        public TimeSpan? TimeLimit { get; init; }

        /// <summary>
        /// Gets the most rows the run may leave held by the scope itself (not the rows it reads from its ancestors), as
        /// estimated from row counts, every table and the union-find included. <c>null</c>, the default, for no limit;
        /// must not be negative. Checked before every iteration: once exceeded, the run stops with
        /// <see cref="RunStopReason.RowLimit"/>, so it overshoots by at most one iteration.
        /// </summary>
        public long? RowLimit { get; init; }

        /// <summary>
        /// Gets a callback that receives every step of the run as it happens, on the run's thread, between engine
        /// calls. It may read the scopes of the run (<c>Check</c>, <c>Get</c>, <c>CountRows</c>, <c>Rows</c>, queries,
        /// extraction); any write or run from it throws <see cref="InvalidOperationException"/>. An exception it throws
        /// propagates out of the run and does not fault the scope. <c>null</c>, the default, for none.
        /// </summary>
        public Action<RunStep>? Observer { get; init; }

        /// <summary>
        /// Gets whether the run keeps every step in <see cref="RunReport.Steps"/>. Off by default.
        /// </summary>
        public bool RecordSteps { get; init; }

        /// <summary>
        /// Gets whether rules run semi-naive (the default) or, for differential tests, each in a naive twin.
        /// </summary>
        internal bool SemiNaive { get; init; } = true;

    }

}
