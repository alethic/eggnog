namespace Eggnog
{

    /// <summary>
    /// One split step in progress (§6.11, R3), on the run's stack of split steps: its scope and report, the lifts already
    /// recorded as steps, and the scope's <see cref="Scope.Version"/> and the time when the last of its cases finished
    /// running. The driver lifts between case runs, so a lift shows as the report's lift count moving between two of
    /// them; the version and the time say whether it changed the scope and how long it took.
    /// </summary>
    internal sealed class SplitStepFrame
    {

        /// <summary>
        /// Initializes a frame for a step starting now.
        /// </summary>
        /// <param name="scope">The scope split.</param>
        /// <param name="report">The step's report, which the driver fills.</param>
        /// <param name="version">The scope's version now.</param>
        /// <param name="timestamp">The time now (<c>Stopwatch.GetTimestamp</c>).</param>
        public SplitStepFrame(Scope scope, Relations.SplitReport report, long version, long timestamp)
        {
            Scope = scope;
            Report = report;
            Version = version;
            Timestamp = timestamp;
        }

        /// <summary>
        /// Gets the scope split.
        /// </summary>
        public Scope Scope { get; }

        /// <summary>
        /// Gets the step's report.
        /// </summary>
        public Relations.SplitReport Report { get; }

        /// <summary>
        /// Gets or sets the number of the report's lifts already recorded as steps.
        /// </summary>
        public int LiftsSeen { get; set; }

        /// <summary>
        /// Gets or sets the scope's version when the window of the next lifts began.
        /// </summary>
        public long Version { get; set; }

        /// <summary>
        /// Gets or sets the time the window of the next lifts began.
        /// </summary>
        public long Timestamp { get; set; }

    }

}
