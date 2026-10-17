using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// The console app's comparison mode, <c>--compare</c>: reads a campaign's exported reports
    /// (<see cref="Campaign"/>), matches each candidate build's cases with the baseline's, and prints each case's
    /// round ratios, spreads and outcome under the campaign's rules (<see cref="CaseComparer"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage: <c>--compare &lt;campaign&gt; --baseline &lt;build&gt; [--candidate &lt;build&gt;]... [--absent
    /// &lt;parameter&gt;=&lt;value&gt;]... [--gate &lt;ratio&gt;] [--spread &lt;fraction&gt;] [--min-time &lt;time&gt;]
    /// [--rounds &lt;n&gt;] [--cases]</c>. Without <c>--candidate</c>, every other build is compared with the baseline.
    /// <c>--cases</c> stops after matching the case lists, which is all a dry run (<c>--job dry</c>) can show.
    /// </para>
    /// <para>
    /// A case of the baseline that a candidate does not run is missing; a case a candidate adds to a class the baseline
    /// has is new; a class only one side has is listed and not compared. The exit code is 0 when no case is missing, 1
    /// when one is, and 2 for a bad command line or campaign.
    /// </para>
    /// </remarks>
    internal static class ComparisonCommand
    {

        /// <summary>
        /// The command-line option that selects this mode; the next argument is the campaign's directory.
        /// </summary>
        public const string Option = "--compare";

        /// <summary>
        /// The usage line printed for a bad command line.
        /// </summary>
        const string Usage = "usage: Eggnog.Benchmarks --compare <campaign> --baseline <build> [--candidate <build>]... "
            + "[--absent <parameter>=<value>]... [--gate <ratio>] [--spread <fraction>] [--min-time <time>] [--rounds <n>] [--cases]";

        /// <summary>
        /// Runs the comparison with the arguments after <see cref="Option"/>.
        /// </summary>
        /// <param name="args">The arguments after <see cref="Option"/>.</param>
        /// <param name="output">Where the comparison is written.</param>
        /// <param name="error">Where errors are written.</param>
        /// <returns>0 when every baseline case has a counterpart in every candidate; 1 when one has none; 2 for a bad
        /// command line or a campaign that cannot be read.</returns>
        public static int Run(ReadOnlySpan<string> args, TextWriter output, TextWriter error)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);

            string? root = null;
            string? baseline = null;
            var candidates = new List<string>();
            var absent = new Dictionary<string, string>(StringComparer.Ordinal);
            var rules = ComparisonRules.Default;
            var casesOnly = false;
            try
            {
                for (var i = 0; i < args.Length; i++)
                {
                    var option = args[i];
                    if (option == "--cases")
                    {
                        casesOnly = true;
                        continue;
                    }

                    if (option.StartsWith("--", StringComparison.Ordinal) == false)
                    {
                        if (root != null)
                            throw new FormatException($"Two campaign directories: '{root}' and '{option}'.");

                        root = option;
                        continue;
                    }

                    if (i + 1 >= args.Length)
                        throw new FormatException($"{option} needs a value.");

                    var value = args[++i];
                    switch (option)
                    {
                        case "--baseline":
                            baseline = value;
                            break;
                        case "--candidate":
                            candidates.Add(value);
                            break;
                        case "--absent":
                            var equals = value.IndexOf('=', StringComparison.Ordinal);
                            if (equals <= 0)
                                throw new FormatException($"--absent takes <parameter>=<value>, not '{value}'.");

                            absent[value.Substring(0, equals)] = value.Substring(equals + 1);
                            break;
                        case "--gate":
                            rules = rules with { Gate = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) };
                            break;
                        case "--spread":
                            rules = rules with { Spread = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) };
                            break;
                        case "--min-time":
                            rules = rules with { MinimumNanoseconds = ReportUnits.ParseNanoseconds(value) };
                            break;
                        case "--rounds":
                            rules = rules with { MaximumRounds = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) };
                            break;
                        default:
                            throw new FormatException($"Unknown option '{option}'.");
                    }
                }

                if (root == null || baseline == null)
                    throw new FormatException("A campaign directory and --baseline are required.");

                rules.Validate();
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException)
            {
                error.WriteLine(e.Message);
                error.WriteLine(Usage);
                return 2;
            }

            Campaign campaign;
            try
            {
                campaign = Campaign.Load(root, absent);
            }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or OverflowException)
            {
                error.WriteLine(e.Message);
                return 2;
            }

            var builds = campaign.Builds.ToList();
            if (builds.Contains(baseline) == false)
            {
                error.WriteLine($"The campaign has no build '{baseline}'; it has: {string.Join(", ", builds)}.");
                return 2;
            }

            if (candidates.Count == 0)
                candidates = builds.Where(b => b != baseline).ToList();

            foreach (var candidate in candidates)
            {
                if (builds.Contains(candidate) == false || candidate == baseline)
                {
                    error.WriteLine($"'{candidate}' is not another build of the campaign; it has: {string.Join(", ", builds)}.");
                    return 2;
                }
            }

            foreach (var skipped in campaign.Skipped)
                output.WriteLine($"Not a run directory (expected r<round>, r<round>-<name>): {skipped}");

            var missing = false;
            foreach (var candidate in candidates)
                missing |= Compare(campaign, baseline, candidate, rules, casesOnly, output);

            return missing ? 1 : 0;
        }

        /// <summary>
        /// Matches one candidate's cases with the baseline's and, unless <paramref name="casesOnly"/>, compares them.
        /// </summary>
        /// <param name="campaign">The campaign.</param>
        /// <param name="baseline">The baseline build.</param>
        /// <param name="candidate">The candidate build.</param>
        /// <param name="rules">The thresholds.</param>
        /// <param name="casesOnly">Whether to stop after matching the case lists.</param>
        /// <param name="output">Where the comparison is written.</param>
        /// <returns><c>true</c> when a baseline case has no counterpart in the candidate.</returns>
        static bool Compare(Campaign campaign, string baseline, string candidate, ComparisonRules rules, bool casesOnly, TextWriter output)
        {
            var baselineRuns = campaign.Runs.Where(r => r.Build == baseline).ToList();
            var candidateRuns = campaign.Runs.Where(r => r.Build == candidate).ToList();
            var baselineCases = CasesByClass(baselineRuns);
            var candidateCases = CasesByClass(candidateRuns);

            var shared = baselineCases.Keys.Intersect(candidateCases.Keys).OrderBy(c => c, StringComparer.Ordinal).ToList();
            var baselineOnly = baselineCases.Keys.Except(candidateCases.Keys).OrderBy(c => c, StringComparer.Ordinal).ToList();
            var candidateOnly = candidateCases.Keys.Except(baselineCases.Keys).OrderBy(c => c, StringComparer.Ordinal).ToList();
            var missingCases = baselineOnly.SelectMany(c => baselineCases[c])
                .Concat(shared.SelectMany(c => baselineCases[c].Except(candidateCases[c])))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            var newCases = shared.SelectMany(c => candidateCases[c].Except(baselineCases[c])).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var matched = shared.SelectMany(c => baselineCases[c].Intersect(candidateCases[c])).OrderBy(k => k, StringComparer.Ordinal).ToList();

            output.WriteLine();
            output.WriteLine($"Baseline {baseline} ({baselineRuns.Count} runs), candidate {candidate} ({candidateRuns.Count} runs): "
                + $"{matched.Count} cases matched in {shared.Count} classes, {missingCases.Count} missing, {newCases.Count} new.");
            foreach (var cls in shared)
                output.WriteLine($"  {cls}: {baselineCases[cls].Intersect(candidateCases[cls]).Count()} matched");

            foreach (var key in missingCases)
                output.WriteLine($"  missing in {candidate}: {key}");

            foreach (var key in newCases)
                output.WriteLine($"  new in {candidate}: {key}");

            if (candidateOnly.Count > 0)
                output.WriteLine($"  classes not in {baseline}, not compared: {string.Join(", ", candidateOnly)}");

            if (casesOnly)
                return missingCases.Count > 0;

            var comparisons = matched.Select(key => CaseComparer.Compare(key, Samples(key, baselineRuns, candidateRuns), rules)).ToList();
            var width = Math.Max(4, comparisons.Count == 0 ? 0 : comparisons.Max(c => c.Key.Length));
            output.WriteLine();
            output.WriteLine($"{"Case".PadRight(width)}  {"Baseline",12}  {"Candidate",12}  Outcome       Rounds: ratio (spread)");
            foreach (var comparison in comparisons)
            {
                var rounds = string.Join("  ", comparison.Rounds.Select(r => FormatRound(r, rules)));
                output.WriteLine($"{comparison.Key.PadRight(width)}  {ReportUnits.FormatNanoseconds(comparison.Baseline),12}  "
                    + $"{ReportUnits.FormatNanoseconds(comparison.Candidate),12}  {comparison.Outcome,-12}  {rounds}");
            }

            output.WriteLine();
            var counts = Enum.GetValues<CaseOutcome>().Select(o => (Outcome: o, Count: comparisons.Count(c => c.Outcome == o))).Where(p => p.Count > 0);
            output.WriteLine("Outcomes: " + string.Join(", ", counts.Select(p => $"{p.Count} {p.Outcome}")) + ".");
            output.WriteLine($"Rules: gate {rules.Gate.ToString(CultureInfo.InvariantCulture)} on cases of at least "
                + $"{ReportUnits.FormatNanoseconds(rules.MinimumNanoseconds)}, same-build runs within "
                + $"{(rules.Spread * 100).ToString("0.###", CultureInfo.InvariantCulture)}% or the round is dropped, "
                + $"{rules.MinimumRounds} agreeing rounds on one side of the gate, at most {rules.MaximumRounds} rounds.");
            return missingCases.Count > 0;
        }

        /// <summary>
        /// Gets the cases each class has in any of <paramref name="runs"/>.
        /// </summary>
        /// <param name="runs">The runs of one build.</param>
        /// <returns>The case keys by class name.</returns>
        static Dictionary<string, HashSet<string>> CasesByClass(IEnumerable<CampaignRun> runs)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var run in runs)
            {
                foreach (var (cls, report) in run.Reports)
                {
                    if (result.TryGetValue(cls, out var keys) == false)
                        result.Add(cls, keys = new HashSet<string>(StringComparer.Ordinal));

                    keys.UnionWith(report.Cases.Select(c => c.Key));
                }
            }

            return result;
        }

        /// <summary>
        /// Gathers one case's medians per round, over the rounds in which both builds have a run with a report of the
        /// case's class.
        /// </summary>
        /// <param name="key">The case.</param>
        /// <param name="baselineRuns">The baseline's runs.</param>
        /// <param name="candidateRuns">The candidate's runs.</param>
        /// <returns>The samples, one entry per round.</returns>
        static List<RoundSamples> Samples(string key, List<CampaignRun> baselineRuns, List<CampaignRun> candidateRuns)
        {
            static List<double> Values(string key, IEnumerable<CampaignRun> runs, int round) =>
                runs.Where(r => r.Round == round)
                    .SelectMany(r => r.Reports.Values)
                    .SelectMany(report => report.Cases)
                    .Where(c => c.Key == key)
                    .Select(c => c.MedianNanoseconds)
                    .ToList();

            var cls = key.Substring(0, key.IndexOf('.', StringComparison.Ordinal));
            var rounds = baselineRuns.Where(r => r.Reports.ContainsKey(cls)).Select(r => r.Round)
                .Intersect(candidateRuns.Where(r => r.Reports.ContainsKey(cls)).Select(r => r.Round))
                .OrderBy(r => r);
            return rounds.Select(round => new RoundSamples(round, Values(key, baselineRuns, round), Values(key, candidateRuns, round))).ToList();
        }

        /// <summary>
        /// Formats one round's ratio and spread, as <c>r1 1.012 (0.4%)</c>, or <c>r1 1.012 (2.1%, dropped)</c> for a
        /// round that is dropped (<see cref="CaseComparer.Agrees"/>); <c>-</c> stands for a ratio or spread that is
        /// unknown, as in <c>r1 1.012 (-, dropped)</c>.
        /// </summary>
        /// <param name="round">The round.</param>
        /// <param name="rules">The thresholds.</param>
        /// <returns>The text.</returns>
        static string FormatRound(RoundResult round, ComparisonRules rules)
        {
            var ratio = double.IsNaN(round.Ratio) ? "-" : round.Ratio.ToString("0.000", CultureInfo.InvariantCulture);
            var spread = double.IsNaN(round.Spread) ? "-" : (round.Spread * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            var dropped = CaseComparer.Agrees(round, rules) ? "" : ", dropped";
            return $"r{round.Round} {ratio} ({spread}{dropped})";
        }

    }

}
