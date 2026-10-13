namespace Eggnog.Relations
{

    /// <summary>
    /// What split steps did (colored-contexts spec §11.3; rule-layer E8): accumulated by <see cref="SplitDriver.Step"/>
    /// and by sources across the steps of a run. Not thread-safe.
    /// </summary>
    internal sealed class SplitReport
    {

        /// <summary>
        /// Gets or sets the number of cases opened.
        /// </summary>
        public int CasesOpened { get; set; }

        /// <summary>
        /// Gets or sets the number of requests not taken: covered by a live record (B6), over a limit, not wanted by the
        /// demand, or declined by their source.
        /// </summary>
        public int SplitsSkipped { get; set; }

        /// <summary>
        /// Gets or sets the number of lifts run.
        /// </summary>
        public int Lifts { get; set; }

        /// <summary>
        /// Gets or sets the candidate keys the lifts skipped over their candidate limit (<see cref="LiftReport.Skipped"/>).
        /// </summary>
        public long LiftRowsSkipped { get; set; }

        /// <inheritdoc />
        public override string ToString() => $"SplitReport {{ CasesOpened = {CasesOpened}, SplitsSkipped = {SplitsSkipped}, Lifts = {Lifts}, LiftRowsSkipped = {LiftRowsSkipped} }}";

    }

}
