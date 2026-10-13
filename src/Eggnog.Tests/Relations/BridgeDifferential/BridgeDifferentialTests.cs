using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The randomized whole-program differential suite of the bridge: random signatures, seeds and rules
    /// (<see cref="BridgeDiffGenerator"/>) run on the bridge with semi-naive rules, on the bridge with naive rules, and
    /// on a naive oracle that re-matches every rule against the whole closed e-graph each iteration
    /// (<see cref="BridgeDiffOracle"/>), compared modulo renaming of ids after the seeds and after every iteration
    /// (<see cref="BridgeDiffRunner"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A disagreement is shrunk (<see cref="BridgeDiffShrinker"/>) and reported with the seed, the failure and the
    /// shrunk program, listed egglog-style.
    /// </para>
    /// <para>
    /// Environment variables: <c>EGGNOG_BRIDGE_DIFF_SEEDS</c> sets how many programs <see cref="RandomProgramsAgree"/>
    /// runs (4000), <c>EGGNOG_BRIDGE_DIFF_BULK_SEEDS</c> how many <see cref="BulkProgramsAgree"/> runs (16);
    /// <c>EGGNOG_BRIDGE_DIFF_SOAK</c> runs <see cref="Soak"/> for that many seconds over fresh seeds,
    /// starting at <c>EGGNOG_BRIDGE_DIFF_SEED_BASE</c> if set (otherwise a random base, printed);
    /// <c>EGGNOG_BRIDGE_DIFF_SEED</c> replays one program (<c>seed</c> or <c>seed:bulk</c>) in <see cref="Replay"/> and
    /// prints it.
    /// </para>
    /// <para>
    /// Programs run in parallel, each on its own e-graphs, so the class runs on its own after the parallel collections.
    /// </para>
    /// </remarks>
    [Collection(ExecutorStressCollection.Name)]
    public class BridgeDifferentialTests
    {

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public BridgeDifferentialTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Runs 4000 programs (by default) and checks that they exercise what the suite is meant to test.
        /// </summary>
        [Fact]
        public void RandomProgramsAgree()
        {
            var clock = Stopwatch.StartNew();
            var stats = RunSeeds(1, Count("EGGNOG_BRIDGE_DIFF_SEEDS", 4000));
            output.WriteLine($"{stats} in {clock.Elapsed.TotalSeconds:0.0} s");

            // The generator must keep reaching what it is meant to exercise.
            Assert.True(stats.ChangedIterations * 3 > stats.Iterations, "too few iterations change anything: " + stats);
            Assert.True(stats.Saturated * 3 > stats.Programs, "too few programs saturate: " + stats);
            Assert.True(stats.Capped > 0, "no program reached the row cap: " + stats);
            Assert.True(stats.RebuildingIterations * 5 > stats.Iterations, "too few iterations union anything: " + stats);
            Assert.True(stats.PartialIterations * 5 > stats.Iterations, "too few iterations run a subset of the rules: " + stats);
            Assert.True(stats.Minted > 20 * stats.Programs, "too few terms built by rules: " + stats);
            Assert.True(stats.MergedIds > 20 * stats.Programs, "too few classes merged: " + stats);
            Assert.True(stats.SubsumedRows > 0, "no row subsumed: " + stats);
            Assert.True(stats.LatticeRows > 0 && stats.RelationRows > 0, "no lattice or relation rows: " + stats);
            Assert.True(stats.KeylessRows > 0 && stats.WideRows > 0, "no rows of functions without keys or with more than three: " + stats);
            Assert.True(stats.LateSeedPrograms > 0, "no late seeds: " + stats);
            Assert.True(stats.ParallelPrograms > 0, "no program ran the parallel rule-set executor: " + stats);
            Assert.True(stats.Panicked > 0 && stats.Panicked * 20 < stats.Programs, "no program panicked, or too many did: " + stats);
            Assert.True(stats.SemiNaiveMatches < stats.NaiveMatches, "semi-naive evaluation matched as much as naive evaluation: " + stats);
            Assert.True(stats.OverBudget * 50 < stats.Programs, "too many programs over the oracle's budget: " + stats);
        }

        /// <summary>
        /// Runs 16 bulk programs (by default), whose tables cross the engine's size thresholds, and checks that they
        /// reach the incremental rebuild and write a lattice value, a relation row and a subsumption for whole bulk
        /// loads, which the parallel ones insert, merging conflicting rows, in parallel.
        /// </summary>
        [Fact]
        public void BulkProgramsAgree()
        {
            var clock = Stopwatch.StartNew();
            var stats = RunSeeds(1_000_000, Count("EGGNOG_BRIDGE_DIFF_BULK_SEEDS", 16), bulk: true);
            output.WriteLine($"{stats} in {clock.Elapsed.TotalSeconds:0.0} s");
            Assert.True(stats.RebuildingIterations > 0, "no bulk iteration rebuilt: " + stats);
            Assert.True(stats.ParallelPrograms > 0, "no bulk program ran with parallel settings: " + stats);
            Assert.True(stats.LatticeRows > 20_000 && stats.RelationRows > 20_000 && stats.SubsumedRows > 20_000, "the bulk writes stayed below the parallel thresholds: " + stats);
        }

        /// <summary>
        /// A time-boxed soak over fresh seeds, for <c>EGGNOG_BRIDGE_DIFF_SOAK</c> seconds; one chunk of programs in ten
        /// is bulk.
        /// </summary>
        [Fact]
        public void Soak()
        {
            var seconds = Count("EGGNOG_BRIDGE_DIFF_SOAK", 0);
            Assert.SkipUnless(seconds > 0, "Set EGGNOG_BRIDGE_DIFF_SOAK to a number of seconds to soak the bridge differential suite.");

            var start = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_BRIDGE_DIFF_SEED_BASE"), out var b) ? b : Random.Shared.Next(1 << 30);
            output.WriteLine($"soaking from seed {start} for {seconds} s");
            var stats = new BridgeDiffStats();
            var clock = Stopwatch.StartNew();
            var next = start;
            var chunks = 0;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                var bulk = ++chunks % 10 == 0;
                var chunk = bulk ? 16 : 2000;
                stats.Add(RunSeeds(next, chunk, bulk));
                next += chunk;
            }

            output.WriteLine($"seeds {start}..{next - 1}: {stats}");
        }

        /// <summary>
        /// Replays the program <c>EGGNOG_BRIDGE_DIFF_SEED</c> names and prints it.
        /// </summary>
        [Fact]
        public void Replay()
        {
            var spec = Environment.GetEnvironmentVariable("EGGNOG_BRIDGE_DIFF_SEED");
            Assert.SkipUnless(string.IsNullOrEmpty(spec) == false, "Set EGGNOG_BRIDGE_DIFF_SEED to a seed (or seed:bulk) to replay one bridge differential program.");

            var parts = spec!.Split(':');
            var program = BridgeDiffGenerator.Generate(int.Parse(parts[0]), parts.Length > 1 && parts[1] == "bulk");
            output.WriteLine(program.ToString());
            var stats = new BridgeDiffStats();
            RunOne(program, stats, parts.Length > 1 && parts[1] == "bulk");
            output.WriteLine(stats.ToString());
        }

        /// <summary>
        /// Runs <paramref name="count"/> programs from seed <paramref name="first"/> in parallel; fails with the
        /// smallest failing seed, shrunk.
        /// </summary>
        static BridgeDiffStats RunSeeds(int first, int count, bool bulk = false)
        {
            var stats = new BridgeDiffStats();
            var failures = new ConcurrentBag<(int Seed, Exception Failure)>();
            var stop = 0;
            Parallel.For(first, first + count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, seed =>
            {
                if (Volatile.Read(ref stop) != 0)
                    return;

                var local = new BridgeDiffStats();
                try
                {
                    BridgeDiffRunner.Run(BridgeDiffGenerator.Generate(seed, bulk), local);
                }
                catch (Exception e)
                {
                    failures.Add((seed, e));
                    Volatile.Write(ref stop, 1);
                }

                stats.Add(local);
            });

            if (failures.IsEmpty == false)
            {
                var (seed, failure) = failures.MinBy(f => f.Seed);
                Report(BridgeDiffGenerator.Generate(seed, bulk), failure, bulk);
            }

            return stats;
        }

        /// <summary>
        /// Runs one program; on a failure, shrinks it and fails with the report.
        /// </summary>
        static void RunOne(BridgeDiffProgram program, BridgeDiffStats stats, bool bulk)
        {
            try
            {
                BridgeDiffRunner.Run(program, stats);
            }
            catch (Exception e)
            {
                Report(program, e, bulk);
            }
        }

        /// <summary>
        /// Shrinks a failing program and fails with the seed, the failure and the shrunk program.
        /// </summary>
        static void Report(BridgeDiffProgram program, Exception failure, bool bulk)
        {
            var (shrunk, shrunkFailure) = BridgeDiffShrinker.Shrink(program, failure);
            var b = new StringBuilder();
            var spec = bulk ? program.Seed + ":bulk" : program.Seed.ToString(CultureInfo.InvariantCulture);
            b.AppendLine($"The bridge disagrees with the oracle on seed {program.Seed}{(bulk ? " (bulk)" : "")}; replay with EGGNOG_BRIDGE_DIFF_SEED={spec}.");
            b.AppendLine(Describe(shrunkFailure));
            b.AppendLine($"Shrunk program ({shrunk.Rules.Length} of {program.Rules.Length} rules, {shrunk.Seeds.Length} of {program.Seeds.Length} seeds):");
            b.Append(shrunk);
            if (ReferenceEquals(shrunkFailure, failure) == false)
                b.AppendLine("Original failure: " + Describe(failure));

            Assert.Fail(b.ToString());
        }

        static string Describe(Exception e) => e is BridgeDiffMismatchException ? e.Message : e.ToString();

        static int Count(string variable, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(variable), out var n) && n > 0 ? n : fallback;

    }

}
