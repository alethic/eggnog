using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Eggnog.Benchmarks.Comparison
{

    /// <summary>
    /// The runs of a measurement campaign, read from a directory laid out as <c>&lt;root&gt;/&lt;build&gt;/&lt;run&gt;/</c>:
    /// one directory per build, and in it one directory per launch of that build's benchmark app (its
    /// <c>--artifacts</c> directory), named <c>r&lt;round&gt;</c> optionally followed by <c>-</c>, <c>_</c> or <c>.</c> and
    /// anything (<c>r1-a</c>, <c>r1-b</c>, <c>r2-a</c>). Every <c>*-report.csv</c> below a run's directory is one of its
    /// reports.
    /// </summary>
    /// <remarks>
    /// A parameter given a value for absent columns is added only to the reports of a class whose other reports, of any
    /// build, have that column: a build that predates a parameter then matches the cases of its default value, and
    /// classes that never had the parameter keep their keys.
    /// </remarks>
    internal sealed class Campaign
    {

        /// <summary>
        /// The pattern of a run directory's name; the group is the round.
        /// </summary>
        static readonly Regex runName = new(@"^r(\d+)(?:[-_.].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="runs">The runs.</param>
        /// <param name="skipped">The directories under a build that are not run directories.</param>
        Campaign(IReadOnlyList<CampaignRun> runs, IReadOnlyList<string> skipped)
        {
            Runs = runs;
            Skipped = skipped;
        }

        /// <summary>
        /// Gets the runs, ordered by build name, round and run name (ordinal).
        /// </summary>
        public IReadOnlyList<CampaignRun> Runs { get; }

        /// <summary>
        /// Gets the directories under a build whose names are not run names; they are not read.
        /// </summary>
        public IReadOnlyList<string> Skipped { get; }

        /// <summary>
        /// Gets the builds, in ordinal order of their names.
        /// </summary>
        public IEnumerable<string> Builds => Runs.Select(r => r.Build).Distinct().OrderBy(b => b, StringComparer.Ordinal);

        /// <summary>
        /// Reads the campaign under <paramref name="root"/>.
        /// </summary>
        /// <param name="root">The campaign's directory.</param>
        /// <param name="absentParameters">The value each named parameter takes in a report without its column, for the
        /// classes some of whose reports have it (<see cref="BenchmarkReport.Parse"/>).</param>
        /// <returns>The campaign.</returns>
        /// <exception cref="DirectoryNotFoundException"><paramref name="root"/> does not exist.</exception>
        /// <exception cref="FormatException">A report does not parse, or a run holds two reports of one class.</exception>
        public static Campaign Load(string root, IReadOnlyDictionary<string, string> absentParameters)
        {
            ArgumentNullException.ThrowIfNull(root);
            ArgumentNullException.ThrowIfNull(absentParameters);
            if (Directory.Exists(root) == false)
                throw new DirectoryNotFoundException($"The campaign directory '{root}' does not exist.");

            // First the runs and their reports, read without absent parameters, which tell the parameter columns each
            // class has in any build.
            var found = new List<(string Build, int Round, string Name, Dictionary<string, (string Text, BenchmarkReport Report)> Reports)>();
            var skipped = new List<string>();
            var none = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var buildDirectory in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                var build = Path.GetFileName(buildDirectory);
                foreach (var runDirectory in Directory.GetDirectories(buildDirectory).OrderBy(d => d, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(runDirectory);
                    var match = runName.Match(name);
                    if (match.Success == false)
                    {
                        skipped.Add(runDirectory);
                        continue;
                    }

                    var reports = new Dictionary<string, (string, BenchmarkReport)>(StringComparer.Ordinal);
                    var files = Directory.GetFiles(runDirectory, "*" + BenchmarkReport.FileSuffix, SearchOption.AllDirectories);
                    foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
                    {
                        var className = BenchmarkReport.ClassOfFile(file);
                        if (className == null)
                            continue;

                        var text = File.ReadAllText(file);
                        if (reports.TryAdd(className, (text, BenchmarkReport.Parse(className, text, none))) == false)
                            throw new FormatException($"The run '{runDirectory}' holds two reports of {className}.");
                    }

                    var round = int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                    found.Add((build, round, name, reports));
                }
            }

            // Then each class's reports again, with the absent parameters that some report of the class has a column for.
            var columns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (cls, (_, report)) in found.SelectMany(r => r.Reports))
            {
                if (columns.TryGetValue(cls, out var names) == false)
                    columns.Add(cls, names = new HashSet<string>(StringComparer.Ordinal));

                names.UnionWith(report.Parameters);
            }

            var runs = new List<CampaignRun>(found.Count);
            foreach (var (build, round, name, reports) in found)
            {
                var parsed = new Dictionary<string, BenchmarkReport>(StringComparer.Ordinal);
                foreach (var (cls, (text, report)) in reports)
                {
                    var absent = absentParameters.Where(p => columns[cls].Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    parsed.Add(cls, absent.Count == 0 ? report : BenchmarkReport.Parse(cls, text, absent));
                }

                runs.Add(new CampaignRun(build, round, name, parsed));
            }

            return new Campaign(runs, skipped);
        }

    }

}
