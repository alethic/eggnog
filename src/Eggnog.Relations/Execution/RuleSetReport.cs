using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// What one <see cref="Database.RunRuleSet"/> did: egglog's <c>RuleSetReport</c>
    /// (egglog-reports/src/lib.rs:64-88).
    /// </summary>
    /// <remarks>
    /// Reports are keyed by rule description. Several rules may share one (the semi-naive variants of a rule do), so
    /// each description maps to the reports of its rules in rule order. Immutable once returned. Public because
    /// <see cref="EGraph.RunRules"/> returns it inside its <see cref="IterationReport"/>; only the engine creates one.
    /// </remarks>
    internal sealed class RuleSetReport
    {

        /// <summary>
        /// The report of an empty rule set: nothing changed and nothing ran.
        /// </summary>
        public static RuleSetReport Empty { get; } = new(false, new Dictionary<string, List<RuleReport>>(), TimeSpan.Zero, TimeSpan.Zero);

        readonly Dictionary<string, List<RuleReport>> ruleReports;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="changed">Whether merging the staged writes changed the database.</param>
        /// <param name="ruleReports">The reports by rule description; owned by the new instance.</param>
        /// <param name="searchAndApplyTime">The time the joins and actions of every rule took.</param>
        /// <param name="mergeTime">The time merging the staged writes took.</param>
        internal RuleSetReport(bool changed, Dictionary<string, List<RuleReport>> ruleReports, TimeSpan searchAndApplyTime, TimeSpan mergeTime)
        {
            ArgumentNullException.ThrowIfNull(ruleReports);
            Changed = changed;
            this.ruleReports = ruleReports;
            SearchAndApplyTime = searchAndApplyTime;
            MergeTime = mergeTime;
        }

        /// <summary>
        /// Gets whether merging the rule set's staged writes changed the database (decision 11: the rule application
        /// alone; rebuilding is reported separately).
        /// </summary>
        public bool Changed { get; }

        /// <summary>
        /// Gets the reports by rule description, each list in rule order.
        /// </summary>
        public IReadOnlyDictionary<string, List<RuleReport>> RuleReports => ruleReports;

        /// <summary>
        /// Gets the time the joins and actions of every rule took, up to the merge.
        /// </summary>
        public TimeSpan SearchAndApplyTime { get; }

        /// <summary>
        /// Gets the time merging the staged writes took.
        /// </summary>
        public TimeSpan MergeTime { get; }

        /// <summary>
        /// Gets the total match count of the rules described <paramref name="rule"/>, zero if there are none. egglog's
        /// <c>num_matches</c>.
        /// </summary>
        /// <param name="rule">The rule description.</param>
        /// <returns>The sum of the rules' match counts.</returns>
        public long NumMatches(string rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (ruleReports.TryGetValue(rule, out var reports) == false)
                return 0;

            long total = 0;
            foreach (var report in reports)
                total += report.NumMatches;

            return total;
        }

        /// <summary>
        /// Gets the total search-and-apply time of the rules described <paramref name="rule"/>. egglog's
        /// <c>rule_search_and_apply_time</c>.
        /// </summary>
        /// <param name="rule">The rule description.</param>
        /// <returns>The sum of the rules' times; zero if there are none.</returns>
        public TimeSpan RuleSearchAndApplyTime(string rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (ruleReports.TryGetValue(rule, out var reports) == false)
                return TimeSpan.Zero;

            var total = TimeSpan.Zero;
            foreach (var report in reports)
                total += report.SearchAndApplyTime;

            return total;
        }

        /// <summary>
        /// Creates the report of the same run completed with the outcome of its merge: <see cref="Database.ExecuteRuleSet"/>
        /// reports before merging and <see cref="Database.MergeRuleSet"/> completes the report. The two share the rule
        /// reports, which neither changes.
        /// </summary>
        /// <param name="changed">Whether merging the staged writes changed the database.</param>
        /// <param name="mergeTime">The time merging the staged writes took.</param>
        /// <returns>The completed report.</returns>
        internal RuleSetReport WithMerge(bool changed, TimeSpan mergeTime) => new(changed, ruleReports, SearchAndApplyTime, mergeTime);

        /// <inheritdoc />
        public override string ToString() => $"changed {Changed}, {ruleReports.Count} rules, search {SearchAndApplyTime.TotalMilliseconds:0.###} ms, merge {MergeTime.TotalMilliseconds:0.###} ms";

    }

}
