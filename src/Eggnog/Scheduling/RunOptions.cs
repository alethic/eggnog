using System;

namespace Eggnog
{

    /// <summary>
    /// How a <see cref="Scope.Run(Schedule, RunOptions, System.Threading.CancellationToken)"/> runs. Limits are per
    /// top-level run and checked before every iteration; a run overshoots a limit or a cancellation by at most one engine
    /// iteration (D42). A limit is not an error: the run stops soundly, with fewer facts, and
    /// <see cref="RunReport.StopReason"/> says why.
    /// </summary>
    public sealed class RunOptions
    {

        /// <summary>
        /// Gets the default options.
        /// </summary>
        public static RunOptions Default { get; } = new();

        /// <summary>
        /// Gets the most engine iterations the run may make; must be at least 0. The default is 10,000.
        /// </summary>
        public int IterationLimit { get; init; } = 10_000;

        /// <summary>
        /// Gets whether the run stops once the scope is contradictory (<see cref="EGraph.Contradiction"/> holds): every
        /// later run step is skipped and the report says <see cref="RunStopReason.Contradiction"/>. Off by default (D11).
        /// </summary>
        public bool StopOnContradiction { get; init; }

        /// <summary>
        /// Gets the longest the run may take, measured from its start; <c>null</c>, the default, for no limit. Must not be
        /// negative. Checked before every iteration: once it has passed, the run stops with
        /// <see cref="RunStopReason.TimeLimit"/>, so it overshoots by at most one iteration (D42).
        /// </summary>
        public TimeSpan? TimeLimit { get; init; }

        /// <summary>
        /// Gets the most rows the run may leave in the own layers of the top-level scope (from R3, and of the run's live
        /// cases): the engine's row-count estimate, every table and the union-find included. <c>null</c>, the default, for
        /// no limit; must not be negative. Checked before every iteration: once exceeded, the run stops with
        /// <see cref="RunStopReason.RowLimit"/>, so it overshoots by at most one iteration (D42).
        /// </summary>
        public long? RowLimit { get; init; }

        /// <summary>
        /// Gets a callback that receives every step of the run as it happens (§6.11), on the run's thread, between engine
        /// calls. It may read the scopes of the run (<c>Check</c>, <c>Get</c>, <c>CountRows</c>, <c>Rows</c>, queries,
        /// extraction); any write or run from it throws <see cref="InvalidOperationException"/>. An exception it throws
        /// propagates out of the run and does not fault the scope. <c>null</c>, the default, for none.
        /// </summary>
        public Action<RunStep>? Observer { get; init; }

        /// <summary>
        /// Gets whether the run keeps every step in <see cref="RunReport.Steps"/> (§6.11). Off by default.
        /// </summary>
        public bool RecordSteps { get; init; }

        /// <summary>
        /// Gets whether rules run semi-naive (the default) or, for differential tests, each in a naive twin (D36).
        /// </summary>
        internal bool SemiNaive { get; init; } = true;

    }

}
