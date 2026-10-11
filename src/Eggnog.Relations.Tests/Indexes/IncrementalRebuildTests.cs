using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the incremental rebuild of a <see cref="SortedWritesTable"/> (egglog's <c>rebuild_incremental</c>,
    /// core-relations/src/table/rebuild.rs:120-195) and for <see cref="Table.RefreshRowsForValues"/> through the
    /// rebuild index: the incremental path must stage exactly what the full scan stages.
    /// </summary>
    [Collection(IndexStressCollection.Name)]
    public class IncrementalRebuildTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        /// <summary>
        /// The ids rows use; the dummy unions live above them.
        /// </summary>
        const int Domain = 4000;

        /// <summary>
        /// Two identical universes see the same random workload of unions, inserts and removals; one rebuilds through
        /// the table's own tracker, which takes the incremental path (the table has more than 10000 rows and each round
        /// unions few ids), the other names the union-find by a fresh id on every call, so it always sees the whole
        /// union-find as new and takes the full path. After every round both tables hold the same rows, in the same
        /// physical order, and both union-finds the same classes. With the non-idempotent sum merge this catches a row
        /// rewritten twice (egglog rewrites a row once per displaced id it mentions).
        /// </summary>
        [Theory]
        [InlineData("union", 1)]
        [InlineData("union", 2)]
        [InlineData("sum", 3)]
        [InlineData("sum", 4)]
        public void IncrementalRebuildEqualsFullRebuild(string merge, int seed)
        {
            var random = new Random(seed);
            var incremental = new RebuildUniverse(merge, ParallelHeuristics.Serial);
            var full = new RebuildUniverse(merge, ParallelHeuristics.Serial);
            var both = new[] { incremental, full };

            // 3000 unions of ids no row uses, so that the full universe's whole union-find is always too large a delta
            // for the incremental heuristic (3000 * 8 exceeds the table).
            var dummies = Enumerable.Range(0, 3000).Select(i => ((uint)(1_000_000 + 2 * i), (uint)(1_000_001 + 2 * i))).ToArray();
            var initial = Enumerable.Range(0, 12_000).Select(_ => RandomRow(random, merge)).ToArray();
            foreach (var u in both)
            {
                u.Union(dummies);
                u.Write(initial, []);
                Assert.True(u.Table.Count > 10_000);
            }

            // The first rebuild sees the dummies and the unions of colliding initial rows: both take the full path.
            Assert.Equal(incremental.RebuildToFixpoint(false), full.RebuildToFixpoint(true));
            for (var round = 0; round < 8; round++)
            {
                var unions = Enumerable.Range(0, random.Next(1, 60)).Select(_ => ((uint)random.Next(Domain), (uint)random.Next(Domain))).ToArray();
                var rows = Enumerable.Range(0, random.Next(0, 150)).Select(_ => RandomRow(random, merge)).ToArray();
                var removals = Enumerable.Range(0, random.Next(0, 40)).Select(_ => R((uint)random.Next(Domain), (uint)random.Next(Domain))).ToArray();
                foreach (var u in both)
                {
                    u.Union(unions);
                    u.Write(rows, removals);
                }

                var calls = incremental.RebuildToFixpoint(false);
                Assert.Equal(calls, full.RebuildToFixpoint(true));
                Assert.Equal(full.Timestamp, incremental.Timestamp);

                var expected = TableTestUtil.LiveRows(full.Table);
                var actual = TableTestUtil.LiveRows(incremental.Table);
                Assert.Equal(expected.Count, actual.Count);
                for (var i = 0; i < expected.Count; i++)
                    Assert.True(expected[i].SequenceEqual(actual[i]), $"round {round} row {i}: {TableTestUtil.Show(actual[i])}, expected {TableTestUtil.Show(expected[i])}");

                Assert.Equal(full.Uf.Count, incremental.Uf.Count);
                for (uint id = 0; id < Domain; id++)
                    Assert.Equal(full.Uf.FindNaive(id), incremental.Uf.FindNaive(id));

                // Every live row is canonical.
                foreach (var row in actual)
                {
                    Assert.Equal(incremental.Uf.FindNaive(row[0]), row[0]);
                    Assert.Equal(incremental.Uf.FindNaive(row[1]), row[1]);
                }
            }

            // The incremental universe used its rebuild index; the full one never needed it.
            Assert.NotNull(incremental.Table.RebuildIndex);
            Assert.Null(full.Table.RebuildIndex);
        }

        static uint[] RandomRow(Random random, string merge) =>
            [(uint)random.Next(Domain), (uint)random.Next(Domain), merge == "union" ? (uint)random.Next(Domain) : (uint)random.Next(1, 6)];

        /// <summary>
        /// A row whose two id columns are both displaced in one round, and whose rewritten key collides with a live
        /// row, is merged into that row once: egglog's incremental path stages it once per displaced id, so the sum
        /// merge would add it twice.
        /// </summary>
        [Fact]
        public void ARowMentioningTwoDisplacedIdsIsRewrittenOnce()
        {
            var u = new RebuildUniverse("sum", ParallelHeuristics.Serial);
            var filler = Enumerable.Range(0, 10_500).Select(i => R((uint)(100_000 + i), (uint)(200_000 + i), 1)).ToList();
            filler.Add(R(10, 20, 5));
            filler.Add(R(1, 2, 7));
            filler.Add(R(10, 10, 3));
            u.Write(filler, []);
            Assert.Equal(1, u.RebuildToFixpoint(false));

            u.Union([(1, 10), (2, 20)]);
            u.RebuildToFixpoint(false);
            Assert.NotNull(u.Table.RebuildIndex);

            var rows = TableTestUtil.LiveRows(u.Table).ToDictionary(r => (r[0], r[1]));
            Assert.Equal(12u, rows[(1, 2)][2]);
            Assert.Equal(3u, rows[(1, 1)][2]);
            Assert.False(rows.ContainsKey((10, 20)));
            Assert.False(rows.ContainsKey((10, 10)));
        }

        /// <summary>
        /// The parallel arm of the incremental rebuild (egglog's <c>par_iter().fold</c>, which needs more than 10000
        /// displaced ids and a table 8192 times larger, too large to reach in a test) stages the same rows as the serial
        /// arm.
        /// </summary>
        [Fact]
        public void RebuildingRowsInParallelMatchesSerially()
        {
            var parallelism = new ParallelHeuristics(4);
            var serial = new RebuildUniverse("union", parallelism);
            var parallel = new RebuildUniverse("union", parallelism);
            var random = new Random(9);
            var rows = Enumerable.Range(0, 9000).Select(_ => R((uint)random.Next(300), (uint)random.Next(3000), (uint)random.Next(300))).ToArray();
            var unions = Enumerable.Range(0, 120).Select(_ => ((uint)random.Next(300), (uint)random.Next(300))).ToArray();
            foreach (var u in new[] { serial, parallel })
            {
                u.Write(rows, []);
                u.Union(unions);
            }

            var rebuilder = serial.Uf.GetRebuilder(serial.Table.RebuildColumns)!;
            var candidates = Enumerable.Range(0, serial.Table.Rows.Count).ToArray();
            var ts = serial.Timestamp;
            Assert.True(serial.Table.RebuildRows(rebuilder, candidates, candidates.Length, ts, serial.State, false));
            Assert.True(parallel.Table.RebuildRows(parallel.Uf.GetRebuilder(parallel.Table.RebuildColumns)!, candidates, candidates.Length, ts, parallel.State, true));
            Assert.False(serial.Table.RebuildRows(rebuilder, candidates, 0, ts, serial.State, false));

            foreach (var u in new[] { serial, parallel })
            {
                u.Table.Merge(u.State);
                u.State.Flush();
                u.Uf.Merge(u.State);
            }

            var expected = TableTestUtil.LiveRows(serial.Table).Select(TableTestUtil.Show).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var actual = TableTestUtil.LiveRows(parallel.Table).Select(TableTestUtil.Show).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, actual);
            for (uint id = 0; id < 3000; id++)
                Assert.Equal(serial.Uf.FindNaive(id), parallel.Uf.FindNaive(id));
        }

        [Fact]
        public void RefreshRowsForValuesUsesTheRebuildIndexAcrossGenerations()
        {
            var u = new RebuildUniverse("union", ParallelHeuristics.Serial);
            u.Write([R(1, 2, 3), R(4, 4, 4), R(5, 6, 7), R(8, 9, 1)], []);
            Assert.Null(u.Table.RebuildIndex);

            // Row (4, 4, 4) holds the dirty id in all three columns: it is restamped once.
            Assert.True(u.Table.RefreshRowsForValues([4, 1], 50));
            Assert.NotNull(u.Table.RebuildIndex);
            u.Table.Merge(u.State);
            var rows = TableTestUtil.LiveRows(u.Table);
            Assert.Equal(4, rows.Count);
            Assert.Equal(50u, rows.Single(r => r[0] == 4)[3]);
            Assert.Equal(50u, rows.Single(r => r[0] == 1)[3]);
            Assert.Equal(50u, rows.Single(r => r[0] == 8)[3]);
            Assert.NotEqual(50u, rows.Single(r => r[0] == 5)[3]);

            // After a compaction the index is rebuilt over the renumbered rows.
            u.Table.Rehash();
            Assert.True(u.Table.RefreshRowsForValues([6], 51));
            Assert.Equal(u.Table.Version, u.Table.RebuildIndex!.UpdatedTo);
            Assert.Equal(1, u.Table.RebuildIndex.UpdatedTo.Major);
            u.Table.Merge(u.State);
            Assert.Equal(51u, TableTestUtil.LiveRows(u.Table).Single(r => r[0] == 5)[3]);
            Assert.False(u.Table.RefreshRowsForValues([99], 52));
        }

        [Fact]
        public void ClonesStartWithoutARebuildIndex()
        {
            var u = new RebuildUniverse("union", ParallelHeuristics.Serial);
            u.Write([R(1, 2, 3), R(2, 3, 4)], []);
            Assert.True(u.Table.RefreshRowsForValues([2], 9));
            Assert.NotNull(u.Table.RebuildIndex);

            var clone = (SortedWritesTable)u.Table.Clone();
            Assert.Null(clone.RebuildIndex);
            Assert.True(clone.RefreshRowsForValues([3], 9));
            Assert.NotNull(clone.RebuildIndex);
            Assert.NotSame(u.Table.RebuildIndex, clone.RebuildIndex);
        }

    }

}
