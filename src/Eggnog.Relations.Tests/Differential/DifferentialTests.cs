using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The randomized differential suite of the executor and actions (milestone M7): random programs of loads,
    /// removals, unions, rebuilds, compaction, cloning and rule runs over random tables, run against the engine and
    /// against a naive model (dictionaries with the merge functions' semantics, a naive union-find, and a nested-loop
    /// join over the visible rows), compared after every step. See <see cref="DiffGenerator"/> for what programs
    /// contain and <see cref="DiffRunner"/> for what is compared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A disagreement is shrunk (<see cref="DiffShrinker"/>) and reported with the seed and the shrunk program.
    /// </para>
    /// <para>
    /// Environment variables: <c>EGGNOG_DIFF_SEEDS</c>, <c>EGGNOG_DIFF_LARGER_SEEDS</c>, <c>EGGNOG_DIFF_BULK_SEEDS</c>,
    /// <c>EGGNOG_DIFF_WIDE_SEEDS</c> and <c>EGGNOG_DIFF_HUGE_SEEDS</c> set how many programs of each profile the
    /// default tests run; <c>EGGNOG_DIFF_SOAK</c> runs <see cref="Soak"/> for that many seconds over fresh seeds of every
    /// profile, starting at <c>EGGNOG_DIFF_SEED_BASE</c> if set (otherwise a random base, printed), and fails a program
    /// that takes longer than <c>EGGNOG_DIFF_SLOW</c> seconds (60), as soon as the limit passes, which is how
    /// pathological plans show up;
    /// <c>EGGNOG_DIFF_SEED</c> replays one program (<c>seed</c> or <c>seed:profile</c>) in <see cref="Replay"/> and
    /// prints it.
    /// </para>
    /// </remarks>
    [Collection(ExecutorStressCollection.Name)]
    public class DifferentialTests
    {

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public DifferentialTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Small programs (two to four tables over two to five values, queries of two to six atoms under all six
        /// strategy and decomposition combinations, some projected, and in one program in ten queries of the
        /// lost-constraint shape), 3000 by default.
        /// </summary>
        [Fact]
        public void SmallProgramsAgreeWithTheModel()
        {
            var stats = RunSeeds(DiffProfile.Small, 0, Count("EGGNOG_DIFF_SEEDS", 3000));
            output.WriteLine(stats.ToString());

            // The generator must keep reaching what it is meant to exercise.
            Assert.True(stats.RulesWithMatches * 4 > stats.Rules, "too few rules have matches: " + stats);
            Assert.True(stats.DecomposedRules > 0, "no decomposed plan: " + stats);
            Assert.True(stats.ProjectedRules > 0, "no projected query: " + stats);
            Assert.True(stats.LookupRules > 0, "no decomposed plan checks a materialization through a Lookup stage (map §8.1 row 5): " + stats);
            Assert.True(stats.MultiplicityChecks > 0, "no multiplicity check: " + stats);
            Assert.True(stats.CachedRules > 0, "no cached plan: " + stats);
            Assert.True(stats.SemiNaiveVariants > stats.SkippedVariants && stats.SkippedVariants > 0, "semi-naive variants not exercised: " + stats);
            Assert.True(stats.ChangingRebuildPasses > 0, "no rebuild changed anything: " + stats);
            Assert.True(stats.SkippedQueries * 20 < stats.Rules, "too many queries over the oracle's budget: " + stats);
        }

        /// <summary>
        /// Larger tables (up to 120 rows per load) and queries of two to four atoms, so the executor's size thresholds
        /// are crossed and tables compact.
        /// </summary>
        [Fact]
        public void LargerProgramsAgreeWithTheModel()
        {
            var stats = RunSeeds(DiffProfile.Larger, 1_000_000, Count("EGGNOG_DIFF_LARGER_SEEDS", 150));
            output.WriteLine(stats.ToString());
            Assert.True(stats.Generations > 0, "no table compacted: " + stats);
            Assert.True(stats.RulesWithMatches * 3 > stats.Rules, "too few rules have matches: " + stats);
        }

        /// <summary>
        /// Bulk tables (hundreds of rows) and queries of two or three atoms, past the index and executor size thresholds,
        /// and queries of the lost-constraint shape, whose decomposed plans materialize bulk-sized blocks.
        /// </summary>
        [Fact]
        public void BulkProgramsAgreeWithTheModel()
        {
            var stats = RunSeeds(DiffProfile.Bulk, 2_000_000, Count("EGGNOG_DIFF_BULK_SEEDS", 20));
            output.WriteLine(stats.ToString());
            Assert.True(stats.RulesWithMatches * 3 > stats.Rules, "too few rules have matches: " + stats);
            Assert.True(stats.DecomposedRules > 0, "no decomposed plan: " + stats);
        }

        /// <summary>
        /// Wide tables and long queries: more than 64 variables per rule, many stages, deep decompositions.
        /// </summary>
        [Fact]
        public void WideProgramsAgreeWithTheModel()
        {
            var stats = RunSeeds(DiffProfile.Wide, 3_000_000, Count("EGGNOG_DIFF_WIDE_SEEDS", 60));
            output.WriteLine(stats.ToString());
            Assert.True(stats.MaxVariables > 64, "no rule had more than 64 variables: " + stats);
            Assert.True(stats.RulesWithMatches > 0, "no rule had matches: " + stats);
        }

        /// <summary>
        /// Huge tables with a union-find, past the parallel and incremental thresholds of inserts, rebuilds and merges.
        /// </summary>
        [Fact]
        public void HugeProgramsAgreeWithTheModel()
        {
            var stats = RunSeeds(DiffProfile.Huge, 4_000_000, Count("EGGNOG_DIFF_HUGE_SEEDS", 3));
            output.WriteLine(stats.ToString());
            Assert.True(stats.RulesWithMatches > 0, "no rule had matches: " + stats);
        }

        /// <summary>
        /// A time-boxed soak over fresh seeds of every profile, for <c>EGGNOG_DIFF_SOAK</c> seconds. A program still
        /// running after <c>EGGNOG_DIFF_SLOW</c> seconds fails the soak at once, with its seed and listing, and is left
        /// running on its thread-pool thread (a pathological plan may never finish).
        /// </summary>
        [Fact]
        public void Soak()
        {
            var seconds = Count("EGGNOG_DIFF_SOAK", 0);
            Assert.SkipUnless(seconds > 0, "Set EGGNOG_DIFF_SOAK to a number of seconds to soak the differential suite.");

            var start = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_DIFF_SEED_BASE"), out var b) ? b : Random.Shared.Next(1 << 30);
            var slow = TimeSpan.FromSeconds(Count("EGGNOG_DIFF_SLOW", 60));
            output.WriteLine($"soaking from seed {start} for {seconds} s");
            var stats = new DiffStats();
            var clock = Stopwatch.StartNew();
            var seed = start;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                var profile = seed % 200 == 199 ? DiffProfile.Huge
                    : seed % 50 == 49 ? DiffProfile.Bulk
                    : seed % 50 == 24 ? DiffProfile.Wide
                    : seed % 10 == 9 ? DiffProfile.Larger
                    : DiffProfile.Small;
                RunOne(profile, seed, stats, slow);

                seed++;
            }

            output.WriteLine($"seeds {start}..{seed - 1}: {stats}");
        }

        /// <summary>
        /// Replays the program <c>EGGNOG_DIFF_SEED</c> names (<c>seed</c> or <c>seed:profile</c>) and prints it.
        /// </summary>
        [Fact]
        public void Replay()
        {
            var spec = Environment.GetEnvironmentVariable("EGGNOG_DIFF_SEED");
            Assert.SkipUnless(string.IsNullOrEmpty(spec) == false, "Set EGGNOG_DIFF_SEED to seed or seed:profile to replay one differential program.");

            var parts = spec!.Split(':');
            var profile = parts.Length > 1 ? DiffProfile.ByName(parts[1]) : DiffProfile.Small;
            var program = DiffGenerator.Generate(int.Parse(parts[0]), profile);
            output.WriteLine(program.Summarize());

            // Run once directly for the step times, then through RunOne, which shrinks and reports a failure.
            var stats = new DiffStats();
            var runner = new DiffRunner(program, profile, stats);
            try
            {
                runner.Run();
            }
            catch (Exception e)
            {
                output.WriteLine($"step {runner.CurrentStep} failed: {e.Message}");
            }

            for (var i = 0; i < runner.StepTimes.Count; i++)
                output.WriteLine($"step {i}: {runner.StepTimes[i].TotalMilliseconds:0} ms");

            output.WriteLine(stats.ToString());
            RunOne(profile, program.Seed, new DiffStats());
        }

        /// <summary>
        /// Runs <paramref name="count"/> programs of <paramref name="profile"/> from seed <paramref name="first"/>.
        /// </summary>
        static DiffStats RunSeeds(DiffProfile profile, int first, int count)
        {
            var stats = new DiffStats();
            for (var seed = first; seed < first + count; seed++)
                RunOne(profile, seed, stats);

            return stats;
        }

        /// <summary>
        /// Runs one program; on a disagreement, shrinks it and fails with the seed, the failure and the shrunk program.
        /// With <paramref name="limit"/>, the program runs on a thread-pool thread and fails as soon as it has run that
        /// long, before shrinking; a disagreement found in time is shrunk without a limit.
        /// </summary>
        static void RunOne(DiffProfile profile, int seed, DiffStats stats, TimeSpan? limit = null)
        {
            var program = DiffGenerator.Generate(seed, profile);
            Exception? e = null;
            if (limit is { } l)
            {
                var run = Task.Run(() => new DiffRunner(program, profile, stats).Run());
                if (Task.WaitAny([run], l) < 0)
                {
                    Assert.Fail($"Seed {seed} (profile {profile.Name}) is still running after the {l.TotalSeconds} s limit; " +
                        $"replay with EGGNOG_DIFF_SEED={seed}:{profile.Name}.{Environment.NewLine}{program.Summarize()}");
                }

                e = run.Exception?.InnerException;
            }
            else
            {
                try
                {
                    new DiffRunner(program, profile, stats).Run();
                }
                catch (Exception failed)
                {
                    e = failed;
                }
            }

            if (e is not null)
            {
                var (shrunk, failure) = DiffShrinker.Shrink(program, profile, e);
                var b = new StringBuilder();
                b.AppendLine($"The engine disagrees with the model on seed {seed} (profile {profile.Name}); replay with EGGNOG_DIFF_SEED={seed}:{profile.Name}.");
                b.AppendLine(failure is DiffMismatchException ? failure.Message : failure.ToString());
                b.AppendLine($"Shrunk program ({shrunk.Steps.Count} of {program.Steps.Count} steps):");
                b.Append(shrunk);
                if (ReferenceEquals(failure, e) == false)
                    b.AppendLine("Original failure: " + (e is DiffMismatchException ? e.Message : e.ToString()));

                Assert.Fail(b.ToString());
            }
        }

        static int Count(string variable, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(variable), out var n) && n > 0 ? n : fallback;

    }

}
