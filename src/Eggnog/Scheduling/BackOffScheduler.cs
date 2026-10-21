using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// egglog-experimental's back-off scheduler (<c>(back-off :match-limit 1000 :ban-length 5)</c>,
    /// <c>src/scheduling.rs</c> of egglog-experimental 1.0.0, the release for egglog 3.0.0): egg's back-off. A step fires
    /// all of a rule's matches unless there are more than its limit; then it bans the rule for a number of steps and doubles
    /// both, so that a rule that keeps matching too much is held back longer each time while the others run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rule's limit is <see cref="MatchLimit"/> doubled for each ban so far, and a ban lasts <see cref="BanLength"/>
    /// doubled likewise. A banned rule's query does not run, so the matches it had wait, and are offered again when the ban
    /// ends. When a step changes nothing while rules are banned, <see cref="CanStop"/> fast-forwards the bans by the
    /// shortest one left, unbanning at least one rule, and says the run cannot stop yet; it forgets the counts of the
    /// rules that are not banned, as egglog-experimental's does.
    /// </para>
    /// <para>
    /// The counts are by rule name. A ban shift past 63 doublings throws, where egglog-experimental panics.
    /// </para>
    /// </remarks>
    public sealed class BackOffScheduler : IRuleScheduler
    {

        readonly Dictionary<string, BackOffRuleStats> stats;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="matchLimit">The most matches a rule may have for a step to fire them before its first ban:
        /// <c>:match-limit</c>.</param>
        /// <param name="banLength">The steps a rule's first ban lasts: <c>:ban-length</c>.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value is negative.</exception>
        public BackOffScheduler(long matchLimit = 1000, long banLength = 5)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(matchLimit);
            ArgumentOutOfRangeException.ThrowIfNegative(banLength);
            MatchLimit = matchLimit;
            BanLength = banLength;
            stats = new Dictionary<string, BackOffRuleStats>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Initializes a copy.
        /// </summary>
        BackOffScheduler(BackOffScheduler other)
        {
            MatchLimit = other.MatchLimit;
            BanLength = other.BanLength;
            stats = new Dictionary<string, BackOffRuleStats>(other.stats.Count, StringComparer.Ordinal);
            foreach (var (rule, counts) in other.stats)
                stats.Add(rule, counts.Copy());
        }

        /// <summary>
        /// Gets the most matches a rule may have before its first ban.
        /// </summary>
        public long MatchLimit { get; }

        /// <summary>
        /// Gets the steps a rule's first ban lasts.
        /// </summary>
        public long BanLength { get; }

        /// <inheritdoc />
        public bool CanStop(IReadOnlyList<string> rules, string ruleset)
        {
            ArgumentNullException.ThrowIfNull(rules);

            // Every rule's counts are taken out, and only the banned ones put back.
            var banned = new List<(string Rule, BackOffRuleStats Stats)>();
            foreach (var rule in rules)
            {
                if (stats.Remove(rule, out var counts) && counts.BannedUntil > counts.Iteration)
                    banned.Add((rule, counts));
            }

            var result = true;
            if (banned.Count > 0)
            {
                var delta = ulong.MaxValue;
                foreach (var (_, counts) in banned)
                    delta = Math.Min(delta, counts.BannedUntil - counts.Iteration);

                foreach (var (_, counts) in banned)
                    counts.BannedUntil -= delta;

                result = false;
            }

            foreach (var (rule, counts) in banned)
                stats[rule] = counts;

            return result;
        }

        /// <inheritdoc />
        public bool FilterMatches(string rule, string ruleset, RuleMatches matches)
        {
            ArgumentNullException.ThrowIfNull(rule);
            ArgumentNullException.ThrowIfNull(matches);
            if (stats.TryGetValue(rule, out var counts) == false)
            {
                counts = new BackOffRuleStats { MatchLimit = (ulong)MatchLimit, BanLength = (ulong)BanLength };
                stats.Add(rule, counts);
            }

            counts.Iteration++;
            if (counts.Iteration < counts.BannedUntil)
                return false;

            // Rust's checked_shl fails only for a shift of the word's width or more; the bits shifted out are lost.
            if (counts.TimesBanned >= 64)
                throw new InvalidOperationException(
                    $"Rule '{rule}' was banned {counts.TimesBanned} times; its match limit cannot double again.");

            var shift = (int)counts.TimesBanned;
            var threshold = counts.MatchLimit << shift;
            if ((ulong)matches.Count > threshold)
            {
                var ban = counts.BanLength << shift;
                counts.TimesBanned++;
                counts.BannedUntil = counts.Iteration + ban;
                return false;
            }

            counts.TimesApplied++;
            matches.ChooseAll();
            return true;
        }

        /// <inheritdoc />
        public IRuleScheduler Clone() => new BackOffScheduler(this);

    }

}
