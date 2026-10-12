namespace Eggnog
{

    /// <summary>
    /// How a <see cref="Scope.Run(Schedule, RunOptions, System.Threading.CancellationToken)"/> runs. Limits are per
    /// top-level run and checked before every iteration; a run overshoots a limit or a cancellation by at most one engine
    /// iteration (D42).
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
        /// Gets whether rules run semi-naive (the default) or, for differential tests, each in a naive twin (D36).
        /// </summary>
        internal bool SemiNaive { get; init; } = true;

    }

}
