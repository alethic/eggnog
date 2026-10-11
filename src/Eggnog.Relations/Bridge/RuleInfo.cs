using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// What an <see cref="EGraph"/> keeps about a registered rule: egglog-bridge's <c>RuleInfo</c>
    /// (egglog-bridge/src/lib.rs:966-972). The recorded query, its plan once the rule first runs, its description, and
    /// the timestamp of its last run, the semi-naive watermark: rows stamped at or after it are new to the rule.
    /// </summary>
    /// <remarks>
    /// <see cref="LastRunAt"/> starts at zero, below every timestamp (the counter starts at one), so a rule's first run
    /// sees every row as new. A run of a set of rules records the run's timestamp in each of them; a rule left out of a
    /// run keeps its watermark, so its next run still sees everything since (the scheduler relies on this, map §3.8).
    /// Not thread-safe.
    /// </remarks>
    internal sealed class RuleInfo
    {

        /// <summary>
        /// Initializes a new instance that has never run.
        /// </summary>
        /// <param name="query">The recorded rule.</param>
        /// <param name="description">The rule's description, which keys its reports.</param>
        public RuleInfo(BridgeQuery query, string description)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(description);
            Query = query;
            Description = description;
        }

        /// <summary>
        /// Gets or sets the timestamp of the rule's last run: the lower bound of what is new to it. egglog's
        /// <c>last_run_at</c>.
        /// </summary>
        public uint LastRunAt { get; set; }

        /// <summary>
        /// Gets the recorded rule.
        /// </summary>
        public BridgeQuery Query { get; }

        /// <summary>
        /// Gets or sets the rule's plan, built at its first run; <c>null</c> before. egglog's <c>cached_plan</c>.
        /// </summary>
        public CachedPlanInfo? CachedPlan { get; set; }

        /// <summary>
        /// Gets the rule's description, which keys its reports (every variant of the rule shares it).
        /// </summary>
        public string Description { get; }

        /// <inheritdoc />
        public override string ToString() => $"{Description} (last run at {LastRunAt}{(CachedPlan is null ? ", not planned" : "")})";

    }

}
