using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// What one run of one rule did: egglog's <c>RuleReport</c> (egglog-reports/src/lib.rs:56-62).
    /// </summary>
    /// <remarks>
    /// egglog reports the plan as a structured value and has no report for decomposed plans (its
    /// <c>Plan::to_report</c> is a <c>todo!()</c> for them); here the plan is the template's text, for both kinds.
    /// Immutable. Public because <see cref="RuleSetReport"/> holds it; only the engine creates one.
    /// </remarks>
    internal sealed class RuleReport
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="plan">The plan's text, or <c>null</c> at <see cref="ReportLevel.TimeOnly"/>.</param>
        /// <param name="searchAndApplyTime">The time the rule's join and actions took.</param>
        /// <param name="numMatches">The number of matches that survived the rule's action program.</param>
        internal RuleReport(string? plan, TimeSpan searchAndApplyTime, long numMatches)
        {
            Plan = plan;
            SearchAndApplyTime = searchAndApplyTime;
            NumMatches = numMatches;
        }

        /// <summary>
        /// Gets the plan's text, or <c>null</c> when the report level did not ask for it.
        /// </summary>
        public string? Plan { get; }

        /// <summary>
        /// Gets the time the rule's join, and the action batches it ran inline, took. In parallel mode batches and
        /// morsels handed to other threads are not counted, as in egglog.
        /// </summary>
        public TimeSpan SearchAndApplyTime { get; }

        /// <summary>
        /// Gets the number of matches that reached the end of the rule's action program. Matches are a multiset, so
        /// the count depends on the plan (decision 6: parity with egglog is not required).
        /// </summary>
        public long NumMatches { get; }

        /// <inheritdoc />
        public override string ToString() => $"{NumMatches} matches in {SearchAndApplyTime.TotalMilliseconds:0.###} ms";

    }

}
