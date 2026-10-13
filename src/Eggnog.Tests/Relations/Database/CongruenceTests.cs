using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Random programs run against a <see cref="Database"/> driven like egglog-bridge and against the naive
    /// <see cref="ReferenceView"/> oracle, checked at random points and at the end (<see cref="CongruenceHarness"/>).
    /// </summary>
    /// <remarks>
    /// The id pool is kept small so that keys collide and congruence cascades. Unions and inserts are batched between
    /// flushes; lookups flush first, as the bridge's <c>add_term</c> does. Half the programs make the function tables
    /// read the union-find (a stratum above it), half only write it (the same stratum).
    /// </remarks>
    public class CongruenceTests
    {

        /// <summary>
        /// The seeds to run: 300 by default, or as many as <c>EGGNOG_DATABASE_SEEDS</c> says, for a longer soak.
        /// </summary>
        public static TheoryData<int> Seeds()
        {
            var count = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_DATABASE_SEEDS"), out var n) && n > 0 ? n : 300;
            var data = new TheoryData<int>();
            for (var seed = 0; seed < count; seed++)
                data.Add(seed);

            return data;
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void RandomProgramsAgreeWithTheOracle(int seed)
        {
            // Half the programs shard their tables (eight shards), which changes staging and merge order but not the
            // result; the operations stay below the parallel thresholds.
            Run(seed, 150, seed % 2 == 0, (seed / 2) % 2 == 0 ? ParallelHeuristics.Serial : new ParallelHeuristics(4));
        }

        /// <summary>
        /// Longer programs, long enough that tables compact (a new generation, so cached indexes rebuild in full) and
        /// clears happen several times.
        /// </summary>
        [Fact]
        public void LongProgramsAgreeWithTheOracle()
        {
            var generations = 0;
            for (var seed = 0; seed < 24; seed++)
                generations = Math.Max(generations, Run(100_000 + seed, 800, seed % 2 == 1, seed % 3 == 0 ? new ParallelHeuristics(4) : ParallelHeuristics.Serial));

            Assert.True(generations > 0, "no program compacted or cleared a table");
        }

        /// <summary>
        /// Programs large enough for every parallel path: database-level merges and per-table rebuilds above 10000
        /// rows, parallel inserts above 20000, parallel and incremental table rebuilds.
        /// </summary>
        [Fact]
        public void BulkProgramsAgreeWhenParallel()
        {
            foreach (var readUnionFind in new[] { false, true })
            {
                var random = new Random(readUnionFind ? 11 : 7);
                var harness = new CongruenceHarness(new ParallelHeuristics(4), readUnionFind, 3);
                try
                {
                    for (var i = 0; i < 4000; i++)
                        harness.NewId();

                    var ids = harness.Ids;
                    uint Pick() => ids[random.Next(ids.Count)];
                    harness.InsertMany(harness.Pair, Enumerable.Range(0, 24_000).Select(_ => new[] { Pick(), Pick(), Pick() }).ToArray());
                    // Distinct wrap keys: random ones would collide often enough to collapse the whole graph into one
                    // class.
                    harness.InsertMany(harness.Wrap, Enumerable.Range(0, 3_000).Select(i => new[] { ids[i], Pick() }).ToArray());
                    harness.InsertMany(harness.Low, Enumerable.Range(0, 3_000).Select(_ => new[] { Pick(), (uint)random.Next(100) }).ToArray());
                    harness.Flush();
                    Assert.True(harness.Db.Parallelism.ParallelizeDbLevelOp(harness.Db.TotalSizeEstimate));
                    harness.Check(48);

                    for (var round = 0; round < 6; round++)
                    {
                        var unions = round % 2 == 0 ? 2 : 12;
                        for (var u = 0; u < unions; u++)
                            harness.Union(Pick(), Pick());

                        harness.InsertMany(harness.Tag, Enumerable.Range(0, 50).Select(_ => new[] { Pick(), (uint)random.Next(3) }).ToArray());
                        harness.InsertMany(harness.Low, Enumerable.Range(0, 50).Select(_ => new[] { Pick(), (uint)random.Next(100) }).ToArray());
                        harness.Flush();
                        if (round == 2)
                            harness.Check(48);
                    }

                    harness.Check(48);
                    Assert.True(harness.RebuildPasses > 6);

                    // The rounds with few unions took the incremental rebuild, which builds the rebuild index.
                    Assert.NotNull(((SortedWritesTable)harness.Db.GetTable(harness.TableOf(harness.Pair))).RebuildIndex);
                }
                catch (Exception e)
                {
                    throw new Xunit.Sdk.XunitException($"readUnionFind={readUnionFind}: {e}");
                }
            }
        }

        static int Run(int seed, int steps, bool readUnionFind, ParallelHeuristics parallelism)
        {
            var random = new Random(seed);
            var harness = new CongruenceHarness(parallelism, readUnionFind, seed);
            try
            {
                for (var step = 0; step < steps; step++)
                    Step(random, harness);

                harness.Check();
                return harness.MaxGeneration;
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed}: {e.Message}{Environment.NewLine}{e.StackTrace}{Environment.NewLine}Program:{Environment.NewLine}{harness.Trace}");
            }
        }

        static void Step(Random random, CongruenceHarness harness)
        {
            var ids = harness.Ids;
            uint Pick() => ids[random.Next(ids.Count)];

            var roll = random.Next(100);
            if (ids.Count < 2 || (roll < 12 && ids.Count < 14))
            {
                harness.NewId();
                return;
            }

            switch (roll)
            {
                case < 26:
                    harness.Union(Pick(), Pick());
                    break;
                case < 44:
                    harness.GetOrAdd(harness.Pair, [Pick(), Pick()]);
                    break;
                case < 52:
                    harness.GetOrAdd(harness.Wrap, [Pick()]);
                    break;
                case < 58:
                    harness.Insert(harness.Pair, [Pick(), Pick(), Pick()]);
                    break;
                case < 66:
                    harness.Insert(harness.Low, [Pick(), (uint)random.Next(10)]);
                    break;
                case < 72:
                    harness.Insert(harness.Tag, [Pick(), (uint)random.Next(3)]);
                    break;
                case < 76:
                    harness.GetOrAdd(harness.Named, [(uint)random.Next(4)]);
                    break;
                case < 86:
                    harness.Flush();
                    break;
                case < 90:
                    harness.Rebuild();
                    break;
                case < 98:
                    harness.Check();
                    break;
                default:
                    harness.Clear(harness.Schema[random.Next(harness.Schema.Count)]);
                    break;
            }
        }

    }

}
