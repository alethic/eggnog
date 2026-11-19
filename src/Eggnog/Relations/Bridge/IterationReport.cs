using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// What one iteration of an <see cref="EGraph"/> or an <see cref="EGraphContext"/> did: the rule set's report and
    /// the rebuild that followed. egglog's <c>IterationReport</c> (egglog-reports/src/lib.rs:90-110), plus whether the
    /// rebuild changed anything and how many passes it ran.
    /// </summary>
    /// <remarks>
    /// <see cref="Changed"/> is whether the rule application changed the database, as in egglog; rebuild changes are
    /// reported separately in <see cref="RebuildChanged"/>, and a scheduler decides saturation from both. Immutable;
    /// only the engine creates one.
    /// </remarks>
    internal sealed class IterationReport
    {

        /// <summary>
        /// Initializes a new instance of an iteration that ran no rebuild pass.
        /// </summary>
        /// <param name="ruleSetReport">The rule set's report.</param>
        /// <param name="rebuildTime">The time the rebuild took; zero when no union called for one.</param>
        /// <param name="rebuildChanged">Whether the rebuild changed any table or container.</param>
        internal IterationReport(RuleSetReport ruleSetReport, TimeSpan rebuildTime, bool rebuildChanged)
            : this(ruleSetReport, rebuildTime, rebuildChanged, 0)
        {

        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="ruleSetReport">The rule set's report.</param>
        /// <param name="rebuildTime">The time the rebuild took; zero when no union called for one.</param>
        /// <param name="rebuildChanged">Whether the rebuild changed any table or container.</param>
        /// <param name="rebuildPasses">The number of rebuild passes; zero when no union called for a rebuild.</param>
        internal IterationReport(RuleSetReport ruleSetReport, TimeSpan rebuildTime, bool rebuildChanged, int rebuildPasses)
        {
            ArgumentNullException.ThrowIfNull(ruleSetReport);
            ArgumentOutOfRangeException.ThrowIfNegative(rebuildPasses);
            RuleSetReport = ruleSetReport;
            RebuildTime = rebuildTime;
            RebuildChanged = rebuildChanged;
            RebuildPasses = rebuildPasses;
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

        /// <summary>
        /// Gets the number of passes the rebuild ran, the last one (which changed nothing) included; zero when the run
        /// made no effective union and so did not rebuild.
        /// </summary>
        public int RebuildPasses { get; }

        /// <inheritdoc />
        public override string ToString() => $"changed {Changed}, rebuild changed {RebuildChanged} in {RebuildPasses} pass(es), rebuild {RebuildTime.TotalMilliseconds:0.###} ms; {RuleSetReport}";

    }

}
