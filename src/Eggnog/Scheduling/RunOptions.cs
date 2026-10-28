using System;

namespace Eggnog
{

    /// <summary>
    /// How a <see cref="Scope.Run(Schedule, RunOptions, System.Threading.CancellationToken)"/> runs. Limits are per
    /// top-level run, cases included, and checked before every iteration (the first included) and every split step; a run
    /// overshoots a limit or a cancellation by at most one engine iteration or one lift, neither of which can be
    /// interrupted. A limit is not an error: the run stops soundly, with fewer facts, and
    /// <see cref="RunReport.StopReason"/> says why.
    /// </summary>
    public sealed class RunOptions
    {

        readonly LiftOptions lift = LiftOptions.Default;

        /// <summary>
        /// Gets the default options.
        /// </summary>
        public static RunOptions Default { get; } = new();

        /// <summary>
        /// Gets the most engine iterations the run may make, in every scope of the run, cases included (mirror syncs do not
        /// count); must be at least 0. The default, <see cref="int.MaxValue"/>, is the largest count the iteration counter
        /// holds and in effect sets no limit: as in egglog, a <c>Saturate</c> runs until nothing changes. Since every case's
        /// iterations count, a run that splits uses up a limit faster than the same theory without splits. There is no
        /// budget per case: to bound each case of a split, pass <c>Repeat(n, steps)</c> as its in-case schedule (see
        /// <see cref="Schedule.Split(SplitRule, Schedule)"/>), so that one case that does not terminate cannot use up a
        /// limit that is set before the split step lifts.
        /// </summary>
        public int IterationLimit { get; init; } = int.MaxValue;

        /// <summary>
        /// Gets whether the run stops once the scope is contradictory (<see cref="EGraph.Contradiction"/> holds): every
        /// later run step is skipped and the report says <see cref="RunStopReason.Contradiction"/>. Off by default; it
        /// governs the top-level scope only, since a case always stops on contradiction.
        /// </summary>
        public bool StopOnContradiction { get; init; }

        /// <summary>
        /// Gets the longest the run may take, measured from its start; <c>null</c>, the default, for no limit. Must not be
        /// negative. Checked before every iteration: once it has passed, the run stops with
        /// <see cref="RunStopReason.TimeLimit"/>, so it overshoots by at most one iteration.
        /// </summary>
        public TimeSpan? TimeLimit { get; init; }

        /// <summary>
        /// Gets the most rows the run may leave held by the top-level scope and by the run's live cases themselves (not
        /// the rows they read from their ancestors), as estimated from row counts, every table and the union-find
        /// included. <c>null</c>, the default, for no limit; must not be negative. Checked before every iteration: once
        /// exceeded, the run stops with <see cref="RunStopReason.RowLimit"/>, so it overshoots by at most one
        /// iteration.
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
        /// Gets the most cases one split may open: a split request with more elements is skipped and counted in
        /// <see cref="RunReport.SplitsSkipped"/> (sound: fewer facts). Default 64; at least 1, or <c>Run</c> throws
        /// <see cref="ArgumentOutOfRangeException"/> before any work.
        /// </summary>
        public int CaseLimit { get; init; } = 64;

        /// <summary>
        /// Gets the number of split levels: a split step at depth <c>d</c> opens cases only while <c>d</c> is below it,
        /// and deepens into them (the nested split steps of its in-case schedule) only while <c>d + 1</c> is. The top-level
        /// scope is at depth 0. Default 4; at least 1, or <c>Run</c> throws <see cref="ArgumentOutOfRangeException"/> before
        /// any work.
        /// </summary>
        public int SplitDepthLimit { get; init; } = 4;

        /// <summary>
        /// Gets the limits of the lifts split steps make. Default <see cref="LiftOptions.Default"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">The value is <c>null</c>.</exception>
        public LiftOptions Lift
        {
            get => lift;
            init => lift = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Gets whether rules run semi-naive (the default) or, for differential tests, each in a naive twin.
        /// </summary>
        internal bool SemiNaive { get; init; } = true;

    }

}
