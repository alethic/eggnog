using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// What one iteration of an <see cref="EGraph"/> did: the rule set's report and the rebuild that followed. egglog's
    /// <c>IterationReport</c> (egglog-reports/src/lib.rs:90-110), plus whether the rebuild changed anything.
    /// </summary>
    /// <remarks>
    /// <see cref="Changed"/> is whether the rule application changed the database, as in egglog; rebuild changes are
    /// reported separately in <see cref="RebuildChanged"/>, and a scheduler decides saturation from both (decision 11 in
    /// docs/engine/decisions.md). Immutable. Public because <see cref="EGraph.RunRules"/> returns it; only the engine
    /// creates one.
    /// </remarks>
    public sealed class IterationReport
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="ruleSetReport">The rule set's report.</param>
        /// <param name="rebuildTime">The time the rebuild took; zero when no union called for one.</param>
        /// <param name="rebuildChanged">Whether the rebuild changed any table or container.</param>
        internal IterationReport(RuleSetReport ruleSetReport, TimeSpan rebuildTime, bool rebuildChanged)
        {
            ArgumentNullException.ThrowIfNull(ruleSetReport);
            RuleSetReport = ruleSetReport;
            RebuildTime = rebuildTime;
            RebuildChanged = rebuildChanged;
        }

        /// <summary>
        /// Gets the rule set's report.
        /// </summary>
        public RuleSetReport RuleSetReport { get; }

        /// <summary>
        /// Gets the time the rebuild took; zero when the run made no effective union.
        /// </summary>
        public TimeSpan RebuildTime { get; }

        /// <summary>
        /// Gets whether merging the rule set's writes changed the database. egglog's <c>changed</c>.
        /// </summary>
        public bool Changed => RuleSetReport.Changed;

        /// <summary>
        /// Gets whether the rebuild after the rule set changed any table or container: some pass of the rebuild loop
        /// rewrote, merged or refreshed a row, or merged a container.
        /// </summary>
        public bool RebuildChanged { get; }

        /// <inheritdoc />
        public override string ToString() => $"changed {Changed}, rebuild changed {RebuildChanged}, rebuild {RebuildTime.TotalMilliseconds:0.###} ms; {RuleSetReport}";

    }

}
