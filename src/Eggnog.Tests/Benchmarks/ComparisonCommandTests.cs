using System;
using System.IO;
using System.Linq;

using Eggnog.Benchmarks.Comparison;

using Xunit;

namespace Eggnog.Tests.Benchmarks
{

    /// <summary>
    /// <see cref="ComparisonCommand"/> and <see cref="Campaign"/> over a campaign written to a temporary directory:
    /// the directory layout, the case-list matching, the printed outcomes and the exit codes.
    /// </summary>
    public sealed class ComparisonCommandTests : IDisposable
    {

        readonly string root = Directory.CreateTempSubdirectory("eggnog-compare-").FullName;

        /// <inheritdoc />
        public void Dispose()
        {
            Directory.Delete(root, recursive: true);
        }

        /// <summary>
        /// Writes one run's report of <c>WritesBenchmarks</c>, as <c>--artifacts</c> lays it out.
        /// </summary>
        void WriteRun(string build, string run, string text, string cls = "WritesBenchmarks")
        {
            var results = Path.Combine(root, build, run, "results");
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, $"Eggnog.Relations.Benchmarks.{cls}-report.csv"), text);
        }

        /// <summary>
        /// A report of two cases, one per thread count, at the given medians.
        /// </summary>
        static string Writes(string one, string eight) => ReportCsv.Text(["Threads"],
            ("ParallelInsert", ["1"], one, "632 MB"),
            ("ParallelInsert", ["8"], eight, "649 MB"));

        (int Exit, string Output, string Error) Run(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = ComparisonCommand.Run([root, .. args], output, error);
            return (exit, output.ToString(), error.ToString());
        }

        static string LineOf(string output, string key) =>
            output.Split('\n').Single(l => l.StartsWith(key + " ", StringComparison.Ordinal));

        [Fact]
        public void ComparesTheInterleavedRunsOfTwoRounds()
        {
            // Round 1: baseline, candidate, candidate, baseline; round 2 the same. Threads=1 is 1% faster; Threads=8
            // is 5% slower in both rounds.
            foreach (var round in new[] { "r1", "r2" })
            {
                WriteRun("base", round + "-a", Writes("420 ms", "200 ms"));
                WriteRun("cand", round + "-b", Writes("415.8 ms", "210 ms"));
                WriteRun("cand", round + "-c", Writes("415.8 ms", "210 ms"));
                WriteRun("base", round + "-d", Writes("420 ms", "200 ms"));
            }

            var (exit, output, error) = Run("--baseline", "base");
            Assert.Equal(0, exit);
            Assert.Equal("", error);
            Assert.Contains("Baseline base (4 runs), candidate cand (4 runs): 2 cases matched in 1 classes, 0 missing, 0 new.", output);

            var one = LineOf(output, "WritesBenchmarks.ParallelInsert(Threads=1)");
            Assert.Contains("420.000 ms", one);
            Assert.Contains("415.800 ms", one);
            Assert.Contains("Pass", one);
            Assert.Contains("r1 0.990 (0.0%)  r2 0.990 (0.0%)", one);

            var eight = LineOf(output, "WritesBenchmarks.ParallelInsert(Threads=8)");
            Assert.Contains("Failure", eight);
            Assert.Contains("r1 1.050 (0.0%)  r2 1.050 (0.0%)", eight);
            Assert.Contains("Outcomes: 1 Pass, 1 Failure.", output);
        }

        [Fact]
        public void SpreadComesFromSameBuildRunsOfTheRound()
        {
            WriteRun("base", "r1-a", Writes("100 ms", "100 ms"));
            WriteRun("base", "r1-b", Writes("102 ms", "100 ms"));
            WriteRun("cand", "r1-a", Writes("101 ms", "100 ms"));
            WriteRun("cand", "r1-b", Writes("101 ms", "100 ms"));

            var (exit, output, _) = Run("--baseline", "base");
            Assert.Equal(0, exit);
            Assert.Contains("r1 1.000 (2.0%, dropped)", LineOf(output, "WritesBenchmarks.ParallelInsert(Threads=1)"));
            Assert.Contains("NeedsRuns", LineOf(output, "WritesBenchmarks.ParallelInsert(Threads=1)"));
            Assert.Contains("r1 1.000 (0.0%)", LineOf(output, "WritesBenchmarks.ParallelInsert(Threads=8)"));
        }

        [Fact]
        public void ARoundWithOneRunOfABuildHasNoSpreadAndIsDropped()
        {
            WriteRun("base", "r1", Writes("100 ms", "100 ms"));
            WriteRun("cand", "r1", Writes("101 ms", "100 ms"));

            var (exit, output, _) = Run("--baseline", "base");
            Assert.Equal(0, exit);
            var one = LineOf(output, "WritesBenchmarks.ParallelInsert(Threads=1)");
            Assert.EndsWith("r1 1.010 (-, dropped)", one.TrimEnd());
            Assert.Contains("NeedsRuns", one);
        }

        [Fact]
        public void EachRoundsRatioComesFromThatRoundsRunsOnly()
        {
            // The candidate is 1% slower in round 1 and 5% slower in round 2; the baseline is the same in both.
            foreach (var (round, candidate) in new[] { ("r1", "101 ms"), ("r2", "105 ms") })
            {
                WriteRun("base", round + "-a", Writes("100 ms", "100 ms"));
                WriteRun("cand", round + "-b", Writes(candidate, "100 ms"));
                WriteRun("cand", round + "-c", Writes(candidate, "100 ms"));
                WriteRun("base", round + "-d", Writes("100 ms", "100 ms"));
            }

            var (exit, output, _) = Run("--baseline", "base");
            Assert.Equal(0, exit);
            var one = LineOf(output, "WritesBenchmarks.ParallelInsert(Threads=1)");
            Assert.Contains("r1 1.010 (0.0%)  r2 1.050 (0.0%)", one);
            Assert.Contains("Inconclusive", one);
        }

        [Fact]
        public void ARoundOnlyOneBuildRanIsLeftOut()
        {
            // Both builds ran rounds 1 and 2 at the same speed; only the baseline ran round 3.
            foreach (var round in new[] { "r1", "r2" })
            {
                WriteRun("base", round + "-a", Writes("100 ms", "100 ms"));
                WriteRun("cand", round + "-b", Writes("100 ms", "100 ms"));
                WriteRun("cand", round + "-c", Writes("100 ms", "100 ms"));
                WriteRun("base", round + "-d", Writes("100 ms", "100 ms"));
            }

            WriteRun("base", "r3-a", Writes("100 ms", "100 ms"));
            WriteRun("base", "r3-d", Writes("100 ms", "100 ms"));

            var (exit, output, _) = Run("--baseline", "base");
            Assert.Equal(0, exit);
            var one = LineOf(output, "WritesBenchmarks.ParallelInsert(Threads=1)");
            Assert.Contains("Pass", one);
            Assert.EndsWith("r1 1.000 (0.0%)  r2 1.000 (0.0%)", one.TrimEnd());
            Assert.DoesNotContain("r3", one);
            Assert.Contains("Outcomes: 2 Pass.", output);
        }

        [Fact]
        public void AMissingCaseFailsTheCaseListAndANewOneIsListed()
        {
            WriteRun("base", "r1", Writes("420 ms", "200 ms"));
            WriteRun("cand", "r1", ReportCsv.Text(["Threads"],
                ("ParallelInsert", ["1"], "420 ms", "632 MB"),
                ("ParallelInsert", ["2"], "300 ms", "640 MB")));

            var (exit, output, _) = Run("--baseline", "base", "--cases");
            Assert.Equal(1, exit);
            Assert.Contains("1 cases matched in 1 classes, 1 missing, 1 new.", output);
            Assert.Contains("missing in cand: WritesBenchmarks.ParallelInsert(Threads=8)", output);
            Assert.Contains("new in cand: WritesBenchmarks.ParallelInsert(Threads=2)", output);
            Assert.DoesNotContain("Outcomes:", output);
        }

        [Fact]
        public void AMissingCaseFailsAFullComparisonToo()
        {
            WriteRun("base", "r1", Writes("420 ms", "200 ms"));
            WriteRun("cand", "r1", ReportCsv.Text(["Threads"],
                ("ParallelInsert", ["1"], "420 ms", "632 MB"),
                ("ParallelInsert", ["2"], "300 ms", "640 MB")));

            var (exit, output, _) = Run("--baseline", "base");
            Assert.Equal(1, exit);
            Assert.Contains("missing in cand: WritesBenchmarks.ParallelInsert(Threads=8)", output);
            Assert.Contains("Outcomes: 1 NeedsRuns.", output);
        }

        [Fact]
        public void AnyCandidateWithAMissingCaseSetsTheExitCode()
        {
            // Candidate a lacks a case and is compared before c, which has both.
            WriteRun("a", "r1", ReportCsv.Text(["Threads"], ("ParallelInsert", ["1"], "420 ms", "632 MB")));
            WriteRun("b", "r1", Writes("420 ms", "200 ms"));
            WriteRun("c", "r1", Writes("420 ms", "200 ms"));

            var (exit, output, _) = Run("--baseline", "b", "--cases");
            Assert.Equal(1, exit);
            Assert.True(output.IndexOf("candidate a", StringComparison.Ordinal) < output.IndexOf("candidate c", StringComparison.Ordinal));
        }

        [Fact]
        public void AClassTheCandidateLacksIsMissingAndOneTheBaselineLacksIsNotCompared()
        {
            WriteRun("base", "r1", Writes("420 ms", "200 ms"));
            WriteRun("base", "r1", ReportCsv.Text([], ("Math", [], "6.6 s", "9.6 GB")), "BridgeMathBenchmarks");
            WriteRun("cand", "r1", Writes("420 ms", "200 ms"));
            WriteRun("cand", "r1", ReportCsv.Text([], ("Ac", [], "15.5 s", "15.5 GB")), "BridgeAcBenchmarks");

            var (exit, output, _) = Run("--baseline", "base", "--cases");
            Assert.Equal(1, exit);
            Assert.Contains("missing in cand: BridgeMathBenchmarks.Math", output);
            Assert.Contains("classes not in base, not compared: BridgeAcBenchmarks", output);
        }

        [Fact]
        public void AnAbsentParameterMatchesABuildThatPredatesIt()
        {
            WriteRun("base", "r1", Writes("420 ms", "200 ms"));
            WriteRun("cand", "r1", ReportCsv.Text(["Threads", "Shards"],
                ("ParallelInsert", ["1", "0"], "420 ms", "632 MB"),
                ("ParallelInsert", ["8", "0"], "200 ms", "649 MB"),
                ("ParallelInsert", ["1", "16"], "430 ms", "632 MB"),
                ("ParallelInsert", ["8", "16"], "200 ms", "649 MB")));

            var (unmatched, _, _) = Run("--baseline", "base", "--cases");
            Assert.Equal(1, unmatched);

            var (exit, output, _) = Run("--baseline", "base", "--cases", "--absent", "Shards=0");
            Assert.Equal(0, exit);
            Assert.Contains("2 cases matched in 1 classes, 0 missing, 2 new.", output);
            Assert.Contains("new in cand: WritesBenchmarks.ParallelInsert(Shards=16, Threads=1)", output);
        }

        [Fact]
        public void AnAbsentParameterIsAddedOnlyToClassesThatHaveItSomewhere()
        {
            foreach (var build in new[] { "base", "cand" })
                WriteRun(build, "r1", ReportCsv.Text([], ("Math", [], "6.6 s", "9.6 GB")), "BridgeMathBenchmarks");

            WriteRun("base", "r1", Writes("420 ms", "200 ms"));
            WriteRun("cand", "r1", ReportCsv.Text(["Threads", "Shards"],
                ("ParallelInsert", ["1", "0"], "420 ms", "632 MB"),
                ("ParallelInsert", ["8", "0"], "200 ms", "649 MB")));

            var campaign = Campaign.Load(root, new System.Collections.Generic.Dictionary<string, string> { ["Shards"] = "0" });
            var keys = campaign.Runs.SelectMany(r => r.Reports.Values).SelectMany(r => r.Cases).Select(c => c.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal);
            Assert.Equal(["BridgeMathBenchmarks.Math", "WritesBenchmarks.ParallelInsert(Shards=0, Threads=1)", "WritesBenchmarks.ParallelInsert(Shards=0, Threads=8)"], keys);

            var (exit, output, _) = Run("--baseline", "base", "--absent", "Shards=0");
            Assert.Equal(0, exit);
            Assert.Contains("3 cases matched in 2 classes, 0 missing, 0 new.", output);
        }

        [Fact]
        public void EveryOtherBuildIsACandidateUnlessNamed()
        {
            foreach (var build in new[] { "a", "b", "c" })
                WriteRun(build, "r1", Writes("420 ms", "200 ms"));

            var (_, all, _) = Run("--baseline", "b", "--cases");
            Assert.Contains("Baseline b (1 runs), candidate a (1 runs)", all);
            Assert.Contains("Baseline b (1 runs), candidate c (1 runs)", all);

            var (_, one, _) = Run("--baseline", "b", "--candidate", "c", "--cases");
            Assert.DoesNotContain("candidate a", one);
            Assert.Contains("candidate c", one);
        }

        [Fact]
        public void DirectoriesThatAreNotRunsAreListedAndSkipped()
        {
            WriteRun("base", "r1", Writes("420 ms", "200 ms"));
            WriteRun("base", "notes", Writes("1 ms", "1 ms"));
            WriteRun("cand", "R2_extra", Writes("420 ms", "200 ms"));

            var campaign = Campaign.Load(root, new System.Collections.Generic.Dictionary<string, string>());
            Assert.Equal([("base", 1, "r1"), ("cand", 2, "R2_extra")], campaign.Runs.Select(r => (r.Build, r.Round, r.Name)));
            Assert.Single(campaign.Skipped);
            Assert.EndsWith("notes", campaign.Skipped[0]);

            var (_, output, _) = Run("--baseline", "base", "--cases");
            Assert.Contains("Not a run directory", output);
        }

        [Fact]
        public void ARunNameIsRAndTheRoundThenASeparator()
        {
            WriteRun("base", "r1", Writes("420 ms", "200 ms"));
            WriteRun("base", "r1x", Writes("420 ms", "200 ms"));
            WriteRun("base", "r2.b", Writes("420 ms", "200 ms"));

            var campaign = Campaign.Load(root, new System.Collections.Generic.Dictionary<string, string>());
            Assert.Equal(["r1", "r2.b"], campaign.Runs.Select(r => r.Name));
            Assert.EndsWith("r1x", Assert.Single(campaign.Skipped));
        }

        [Fact]
        public void TwoReportsOfOneClassInARunAreAnError()
        {
            WriteRun("base", "r1", Writes("420 ms", "200 ms"));
            var other = Path.Combine(root, "base", "r1", "other");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "Eggnog.Relations.Benchmarks.WritesBenchmarks-report.csv"), Writes("420 ms", "200 ms"));

            var (exit, _, error) = Run("--baseline", "base");
            Assert.Equal(2, exit);
            Assert.Contains("holds two reports of WritesBenchmarks", error);
        }

        [Fact]
        public void ASecondCampaignDirectoryIsAnError()
        {
            WriteRun("base", "r1", Writes("420 ms", "200 ms"));

            var (exit, _, error) = Run("--baseline", "base", root);
            Assert.Equal(2, exit);
            Assert.Contains("Two campaign directories", error);
        }

        [Fact]
        public void TheThresholdsCanBeSetOnTheCommandLine()
        {
            foreach (var round in new[] { "r1", "r2" })
            {
                WriteRun("base", round + "-a", Writes("100 ms", "50 us"));
                WriteRun("base", round + "-b", Writes("100 ms", "50 us"));
                WriteRun("cand", round + "-a", Writes("103 ms", "60 us"));
                WriteRun("cand", round + "-b", Writes("103 ms", "60 us"));
            }

            var (_, strict, _) = Run("--baseline", "base");
            Assert.Contains("Failure", LineOf(strict, "WritesBenchmarks.ParallelInsert(Threads=1)"));
            Assert.Contains("Reported", LineOf(strict, "WritesBenchmarks.ParallelInsert(Threads=8)"));

            var (_, loose, _) = Run("--baseline", "base", "--gate", "1.05", "--min-time", "10us");
            Assert.Contains("Pass", LineOf(loose, "WritesBenchmarks.ParallelInsert(Threads=1)"));
            Assert.Contains("Failure", LineOf(loose, "WritesBenchmarks.ParallelInsert(Threads=8)"));
            Assert.Contains("Rules: gate 1.05 on cases of at least 10.000 us", loose);
        }

        [Fact]
        public void TheSpreadCanBeSetOnTheCommandLine()
        {
            // The baseline's runs are 2% apart in both rounds: dropped under the default rule, kept under 3%.
            foreach (var round in new[] { "r1", "r2" })
            {
                WriteRun("base", round + "-a", Writes("100 ms", "100 ms"));
                WriteRun("base", round + "-b", Writes("102 ms", "100 ms"));
                WriteRun("cand", round + "-a", Writes("101 ms", "100 ms"));
                WriteRun("cand", round + "-b", Writes("101 ms", "100 ms"));
            }

            var (_, strict, _) = Run("--baseline", "base");
            Assert.Contains("r1 1.000 (2.0%, dropped)", LineOf(strict, "WritesBenchmarks.ParallelInsert(Threads=1)"));
            Assert.Contains("Inconclusive", LineOf(strict, "WritesBenchmarks.ParallelInsert(Threads=1)"));

            var (_, loose, _) = Run("--baseline", "base", "--spread", "0.03");
            var one = LineOf(loose, "WritesBenchmarks.ParallelInsert(Threads=1)");
            Assert.Contains("Pass", one);
            Assert.Contains("r1 1.000 (2.0%)  r2 1.000 (2.0%)", one);
            Assert.Contains("same-build runs within 3%", loose);
        }

        [Theory]
        [InlineData("")]
        [InlineData("--baseline")]
        [InlineData("--baseline base --gate x")]
        [InlineData("--baseline base --gate 0")]
        [InlineData("--baseline base --rounds 1")]
        [InlineData("--baseline base --spread NaN")]
        [InlineData("--baseline base --min-time NA")]
        [InlineData("--baseline base --absent Shards")]
        [InlineData("--baseline base --absent =0")]
        [InlineData("--baseline base --frobnicate 1")]
        [InlineData("--baseline base second-directory")]
        [InlineData("--baseline nobody")]
        [InlineData("--baseline base --candidate base")]
        [InlineData("--baseline base --candidate nobody")]
        public void ABadCommandLineIsExitCodeTwo(string commandLine)
        {
            var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            WriteRun("base", "r1", Writes("420 ms", "200 ms"));
            WriteRun("cand", "r1", Writes("420 ms", "200 ms"));

            var (exit, _, error) = Run(args);
            Assert.Equal(2, exit);
            Assert.NotEqual("", error);
        }

        [Fact]
        public void AMissingCampaignDirectoryIsExitCodeTwo()
        {
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(2, ComparisonCommand.Run([Path.Combine(root, "absent"), "--baseline", "base"], output, error));
            Assert.Contains("does not exist", error.ToString());
        }

        [Fact]
        public void AReportThatDoesNotParseIsExitCodeTwo()
        {
            WriteRun("base", "r1", "Job,Mean\r\nDry,1 ms\r\n");
            var (exit, _, error) = Run("--baseline", "base");
            Assert.Equal(2, exit);
            Assert.Contains("Method", error);
        }

    }

}
