using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The randomized differential suite of the executor and actions: random programs of loads, removals, unions,
    /// rebuilds, compaction, cloning and rule runs over random tables, run against the engine and against a naive model
    /// (dictionaries with the merge functions' semantics, a naive union-find, and a nested-loop join over the visible
    /// rows), compared after every step. See <see cref="DiffGenerator"/> for what programs contain and
    /// <see cref="DiffRunner"/> for what is compared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A disagreement is shrunk (<see cref="DiffShrinker"/>) and reported with the seed and the shrunk program.
    /// </para>
    /// <para>
    /// Environment variables: <c>EGGNOG_DIFF_SEEDS</c>, <c>EGGNOG_DIFF_LARGER_SEEDS</c>, <c>EGGNOG_DIFF_BULK_SEEDS</c>,
    /// <c>EGGNOG_DIFF_WIDE_SEEDS</c> and <c>EGGNOG_DIFF_HUGE_SEEDS</c> set how many programs of each profile the
    /// default tests run; <c>EGGNOG_DIFF_SOAK</c> runs <see cref="Soak"/> for that many seconds over fresh seeds of
    /// every profile, starting at <c>EGGNOG_DIFF_SEED_BASE</c> if set (otherwise a random base, printed), and fails a
    /// program that takes longer than <c>EGGNOG_DIFF_SLOW</c> seconds (60), as soon as the limit passes, which is how
    /// pathological plans show up; <c>EGGNOG_DIFF_SEED</c> replays one program (<c>seed</c> or <c>seed:profile</c>) in
    /// <see cref="Replay"/> and prints it.
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
            Assert.True(stats.LookupRules > 0, "no decomposed plan checks a materialization through a Lookup stage: " + stats);
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
        /// Bulk tables (hundreds of rows) and queries of two or three atoms, past the index and executor size
        /// thresholds, and queries of the lost-constraint shape, whose decomposed plans materialize bulk-sized blocks.
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
        /// Small and larger programs in which every semi-naive step is preceded by the same variants run over a layered
        /// copy of the engine's tables (<see cref="DiffLayeredSemiNaiveStep"/>): rows spread over two to four levels
        /// with their timestamps, overridden rows hidden in lower levels, so each variant's time ranges split per
        /// layer; every variant's distinct matches must be the oracle's. 2000 small and 100 larger programs by default
        /// (<c>EGGNOG_DIFF_LAYERED_SEEDS</c>, <c>EGGNOG_DIFF_LAYERED_LARGER_SEEDS</c>).
        /// </summary>
        [Fact]
        public void LayeredSemiNaiveVariantsAgreeWithTheModel()
        {
            var stats = new DiffStats();
            for (var seed = 6_000_000; seed < 6_000_000 + Count("EGGNOG_DIFF_LAYERED_SEEDS", 2000); seed++)
                RunOne(DiffProfile.Small, seed, stats, layered: true);

            for (var seed = 7_000_000; seed < 7_000_000 + Count("EGGNOG_DIFF_LAYERED_LARGER_SEEDS", 100); seed++)
                RunOne(DiffProfile.Larger, seed, stats, layered: true);

            output.WriteLine(stats.ToString());
            Assert.True(stats.LayeredVariants > 0, "no layered variant ran: " + stats);
            Assert.True(stats.LayeredVariantsWithMatches > 300, "too few layered variants have matches: " + stats);
            Assert.True(stats.LayeredHiddenRows > 0, "no layered copy hid a row: " + stats);
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
        static void RunOne(DiffProfile profile, int seed, DiffStats stats, TimeSpan? limit = null, bool layered = false)
        {
            var program = DiffGenerator.Generate(seed, profile);
            if (layered)
                program = Layered(program);

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

        /// <summary>
        /// Puts a <see cref="DiffLayeredSemiNaiveStep"/> before every semi-naive step of <paramref name="program"/>,
        /// over the same variants and the same tables.
        /// </summary>
        static DiffProgram Layered(DiffProgram program)
        {
            var steps = new List<DiffStep>();
            for (var i = 0; i < program.Steps.Count; i++)
            {
                if (program.Steps[i] is DiffSemiNaiveStep semi)
                    steps.Add(new DiffLayeredSemiNaiveStep(semi, program.Seed * 31 + i));

                steps.Add(program.Steps[i]);
            }

            return program with { Steps = steps };
        }

        static int Count(string variable, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(variable), out var n) && n > 0 ? n : fallback;

        /// <summary>
        /// The generated programs reach materialization lookups. A block with no value variables whose message
        /// variables are split across later bags keeps its join constraint only through a
        /// <see cref="MatScanMode.Lookup"/> stage in the result block, where egglog's planner loses the constraint; a
        /// bag constrained by two earlier blocks gets a Lookup epilogue. Such plans arise only for queries with named
        /// variables the action does not read, which the generator makes by projecting some queries onto a subset of
        /// their variables and by the dense lost-constraint shape. (The lost-constraint query of
        /// PlannerDecompositionTests, with five of its nine variables read, gets a Lookup stage on most random data.)
        /// This test inspects the plans of every rule-set query of the default small programs under every
        /// configuration.
        /// </summary>
        [Fact]
        public void DefaultProgramsReachMaterializationLookups()
        {
            var profile = DiffProfile.Small;
            var decomposed = 0;
            var lookups = 0;
            for (var seed = 0; seed < 3000; seed++)
            {
                var program = DiffGenerator.Generate(seed, profile);
                var runner = new DiffRunner(program, profile, new DiffStats());
                foreach (var step in program.Steps)
                {
                    if (step is DiffRunStep run)
                    {
                        var function = runner.Db.AddExternalFunction(new DiffRecorder().Function);
                        var rsb = runner.Db.NewRuleSet();
                        var rules = new List<RuleId>();
                        foreach (var query in run.Queries)
                            foreach (var config in DiffConfig.All)
                                if (runner.AddRule(rsb, query, config, 0, function, Array.Empty<(int, Constraint)>()) is { } id)
                                    rules.Add(id);

                        using (var rs = rsb.Build())
                        {
                            foreach (var id in rules)
                            {
                                if (rs[id].Template is DecomposedPlan plan)
                                {
                                    decomposed++;
                                    var stages = plan.ResultBlock.Concat(plan.Blocks.SelectMany(b => b.Stages));
                                    if (stages.Any(s => s is FusedIntersectMatStage { Mode: MatScanMode.Lookup }))
                                        lookups++;
                                }
                            }
                        }

                        runner.Db.FreeExternalFunction(function);
                    }

                    step.Execute(runner);
                }
            }

            Assert.True(decomposed > 0, "no decomposed plan at all");
            Assert.True(lookups > 0, $"none of {decomposed} decomposed plans has a materialization Lookup stage");
        }

        /// <summary>
        /// A shrunk failing program is printed for reproduction, and its seed regenerates the original program, not the
        /// shrunk one, so the listing must be complete: <see cref="DiffLoadStep.Format"/> lists every row of a load,
        /// also past the <see cref="DiffLoadStep.FormatLimit"/> rows at which <see cref="DiffLoadStep.Summarize"/>
        /// stops ("the seed reproduces the rest"). A shrunk bulk or huge program may still load more than 200 rows into
        /// one table, and must be rebuildable from its listing.
        /// </summary>
        [Fact]
        public void LoadListingsAreComplete()
        {
            var rows = Enumerable.Range(0, 300).Select(i => new[] { (uint)i, (uint)(i + 1) }).ToArray();
            var program = new DiffProgram(1, "bulk", "Serial", 1000, [new DiffTable(0, 1, 2, false, DiffMergeKind.TakeNew, 1, false, [], false, [], [])], []);
            var listing = new DiffLoadStep(0, rows).Format(program);
            Assert.Contains(TableTestUtil.Show(rows[^1]), listing);
        }

    }

}
