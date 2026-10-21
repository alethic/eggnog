namespace Eggnog
{

    /// <summary>
    /// What a <see cref="BackOffScheduler"/> counts for one rule: egglog-experimental's <c>RuleStats</c>, in
    /// <c>usize</c>, here <c>ulong</c>.
    /// </summary>
    internal sealed class BackOffRuleStats
    {

        /// <summary>
        /// Gets or sets how many steps have offered the rule matches.
        /// </summary>
        public ulong Iteration { get; set; }

        /// <summary>
        /// Gets or sets how many steps fired its matches.
        /// </summary>
        public ulong TimesApplied { get; set; }

        /// <summary>
        /// Gets or sets the step until which it is banned.
        /// </summary>
        public ulong BannedUntil { get; set; }

        /// <summary>
        /// Gets or sets how many times it was banned.
        /// </summary>
        public ulong TimesBanned { get; set; }

        /// <summary>
        /// Gets or sets its match limit before the doubling.
        /// </summary>
        public ulong MatchLimit { get; set; }

        /// <summary>
        /// Gets or sets its ban length before the doubling.
        /// </summary>
        public ulong BanLength { get; set; }

        /// <summary>
        /// Copies the counts.
        /// </summary>
        /// <returns>The copy.</returns>
        public BackOffRuleStats Copy() => (BackOffRuleStats)MemberwiseClone();

    }

}
