using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Randomized model tests of <see cref="Index{TIndex}"/> over a <see cref="SortedWritesTable"/>: random batches of
    /// inserts (new keys and replacements), removals (tombstones), compactions and clears (new generations), with a
    /// one-column, a multi-column and a tuple index refreshed after every merge and checked against a naive group-by of
    /// the live rows; indexes built from scratch, serially and in parallel, must list exactly the live rows.
    /// </summary>
    [Collection(IndexStressCollection.Name)]
    public class IndexPropertyTests
    {

        static uint[] RandomRow(Random random, int keyDomain, int valueDomain, uint ts) =>
            [(uint)random.Next(keyDomain), (uint)random.Next(keyDomain), (uint)random.Next(valueDomain), (uint)random.Next(valueDomain), ts];

        [Theory]
        [InlineData(1, 1)]
        [InlineData(1, 2)]
        [InlineData(1, 3)]
        [InlineData(4, 4)]
        [InlineData(4, 5)]
        public void IncrementalRefreshMatchesNaiveGroupBy(int threads, int seed)
        {
            var random = new Random(seed);
            var harness = new IndexHarness(new ParallelHeuristics(threads));
            var table = harness.Table;
            var state = TableTestUtil.EmptyState();
            uint ts = 0;
            for (var step = 0; step < 80; step++)
            {
                var op = random.Next(20);
                if (op < 12)
                {
                    TableTestUtil.Stage(table, Enumerable.Range(0, random.Next(1, 120)).Select(_ => RandomRow(random, 14, 9, ts)).ToArray());
                }
                else if (op < 18)
                {
                    TableTestUtil.StageRemove(table, Enumerable.Range(0, random.Next(1, 60)).Select(_ => new[] { (uint)random.Next(14), (uint)random.Next(14) }).ToArray());
                }
                else if (op < 19)
                {
                    table.Rehash();
                }
                else
                {
                    table.Clear();
                }

                table.Merge(state);
                ts++;

                // Sometimes the indexes skip a step and catch up over two merges' worth of rows.
                if (random.Next(4) == 0)
                    continue;

                harness.Refresh();
                harness.CheckAgainstModel($"seed {seed} step {step}");
                if (step % 10 == 9)
                    harness.CheckFreshBuilds($"seed {seed} step {step}");
            }
        }

        /// <summary>
        /// Refreshes of more than 20000 rows, incremental and full, go through the parallel build; an index refreshed
        /// in parallel lists exactly what one refreshed serially over the same table lists, stale rows included.
        /// </summary>
        [Fact]
        public void ParallelRefreshMatchesSerialRefresh()
        {
            var random = new Random(77);
            var parallel = new IndexHarness(new ParallelHeuristics(4));
            var table = parallel.Table;
            var serialSingle = new Index<ColumnIndex>(IndexTestUtil.Ids(IndexHarness.SingleColumns), new ColumnIndex(ParallelHeuristics.Serial));
            var serialMulti = new Index<ColumnIndex>(IndexTestUtil.Ids(IndexHarness.MultiColumns), new ColumnIndex(ParallelHeuristics.Serial));
            var serialTuple = new Index<TupleIndex>(IndexTestUtil.Ids(IndexHarness.TupleColumns), new TupleIndex(IndexHarness.TupleColumns.Length, ParallelHeuristics.Serial));
            var state = TableTestUtil.EmptyState();
            uint ts = 0;
            for (var step = 0; step < 6; step++)
            {
                switch (step)
                {
                    case 0:
                    case 1:
                    case 4:
                        TableTestUtil.Stage(table, Enumerable.Range(0, 26_000).Select(_ => RandomRow(random, 400, 3000, ts)).ToArray());
                        break;
                    case 2:
                        TableTestUtil.StageRemove(table, Enumerable.Range(0, 30_000).Select(_ => new[] { (uint)random.Next(400), (uint)random.Next(400) }).ToArray());
                        break;
                    case 3:
                        table.Rehash();
                        break;
                    default:
                        TableTestUtil.Stage(table, Enumerable.Range(0, 500).Select(_ => RandomRow(random, 400, 3000, ts)).ToArray());
                        break;
                }

                table.Merge(state);
                ts++;
                parallel.Refresh();
                serialSingle.Refresh(table);
                serialMulti.Refresh(table);
                serialTuple.Refresh(table);
                parallel.CheckAgainstModel($"step {step}");
                IndexTestUtil.AssertExactly(IndexTestUtil.Entries(serialSingle.Data), IndexTestUtil.Entries(parallel.Single.Data), $"step {step} single");
                IndexTestUtil.AssertExactly(IndexTestUtil.Entries(serialMulti.Data), IndexTestUtil.Entries(parallel.Multi.Data), $"step {step} multi");
                IndexTestUtil.AssertExactly(IndexTestUtil.Entries(serialTuple.Data), IndexTestUtil.Entries(parallel.Tuple.Data), $"step {step} tuple");
            }

            parallel.CheckFreshBuilds("end");
        }

    }

}
