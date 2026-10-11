using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Adversarial review tests for the tables milestone. Each test exposes a defect the review confirmed; they fail
    /// until the implementation is fixed.
    /// </summary>
    public class TablesReviewTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        static readonly ParallelHeuristics Parallel4 = new(4);

        static void StageAll(Table table, IEnumerable<uint[]> rows)
        {
            var buffers = Enumerable.Range(0, 3).Select(_ => table.NewBuffer()).ToArray();
            var i = 0;
            foreach (var row in rows)
                buffers[i++ % buffers.Length].StageInsert(row);

            foreach (var buffer in buffers)
                buffer.Flush();
        }

        static int ShardOf(SortedWritesTable table, uint key) => table.Parallelism.ShardData.ShardOf(TableKeyHash.Hash(R(key), 1));

        /// <summary>
        /// <c>ParallelInsert</c> hands the table's row buffer to a <see cref="ParallelRowBufferWriter"/>
        /// (<c>data.ParallelWriter()</c> empties the buffer object) and only puts the rows back with
        /// <c>data = writer.Finish()</c> after <c>Parallel.For</c> returns normally. Any exception inside a shard task
        /// (here the sort-order pre-pass; equally a merge function that throws) skips that line, so the table keeps an
        /// empty row buffer while its key index still names thousands of rows; later point lookups read past an empty
        /// array through the unchecked <c>Key*Match</c> structs. The serial path stays consistent after the same misuse.
        /// </summary>
        [Fact]
        public void ParallelSortViolationKeepsTheTablesRows()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, Parallel4);
            StageAll(table, Enumerable.Range(0, 30_000).Select(i => R((uint)i, (uint)i, 2)));
            table.Merge(state);
            Assert.Equal(30_000, table.Count);

            // New keys with a timestamp below the last run: every shard's pre-pass rejects them.
            StageAll(table, Enumerable.Range(30_000, 30_000).Select(i => R((uint)i, (uint)i, 1)));
            Assert.ThrowsAny<Exception>(() => table.Merge(state));

            Assert.Equal(30_000, table.Rows.Count);
            Assert.Equal(30_000, table.Count);
            using (new DebugAssertScope())
                Assert.Equal(R(5, 5, 2), table.GetRow(R(5))!.Value.Values);
        }

        /// <summary>
        /// When each shard sees one sort value but the shards disagree, <c>ParallelInsert</c> notices only in its
        /// result loop, after every row was appended, merged and indexed: it throws part-way through adding up
        /// <c>MarkedStale</c> and before recording the sort run. The table is left with an inexact stale count (zero
        /// here, so scans take the no-tombstone fast path and emit tombstones: the stale-row hole of map §8.1 row 1)
        /// and with rows that no timestamp fast subset can see.
        /// </summary>
        [Fact]
        public void ParallelCrossShardSortMismatchLeavesTheTableConsistent()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, Parallel4);
            StageAll(table, Enumerable.Range(0, 30_000).Select(i => R((uint)i, (uint)i, 1)));
            table.Merge(state);
            Assert.Equal(0, table.StaleCount);

            // Shard 0: new keys at timestamp 2. Shard 1: new keys at timestamp 3. Shards 2 and up: existing keys at
            // timestamp 2, which tombstone their old rows. No shard's own check fails; the result loop throws at shard
            // 1, before it adds the stale counts of shards 2 and up.
            var rows = new List<uint[]>();
            var fresh = new[] { 0, 0 };
            for (var key = 30_000u; fresh[0] < 2_000 || fresh[1] < 2_000; key++)
            {
                var shard = ShardOf(table, key);
                if (shard < 2 && fresh[shard] < 2_000)
                {
                    rows.Add(R(key, key, shard == 0 ? 2u : 3u));
                    fresh[shard]++;
                }
            }

            for (var key = 0u; key < 30_000; key++)
            {
                if (ShardOf(table, key) >= 2)
                    rows.Add(R(key, key + 1, 2));
            }

            Assert.True(rows.Count > ParallelHeuristics.TableOpThreshold);
            StageAll(table, rows);
            Assert.ThrowsAny<Exception>(() => table.Merge(state));

            // The stale count must stay exact, and no scan may return a tombstone.
            var tombstones = Enumerable.Range(0, table.Rows.Count).Count(r => table.Rows.IsStale(r));
            Assert.Equal(tombstones, table.StaleCount);
            Assert.DoesNotContain(TableTestUtil.LiveRows(table), row => row[0] == Value.StaleRep);

            // Fast subsets on the sort column must agree with a filtered scan.
            foreach (var ts in new uint[] { 2, 3 })
            {
                Assert.True(table.TryFastSubset(Constraint.GeConst(C(2), ts), out var fast));
                var viaFast = TableTestUtil.DumpSubset(table, fast.AsRef()).Count;
                var viaScan = TableTestUtil.LiveRows(table).Count(r => r[2] >= ts);
                Assert.Equal(viaScan, viaFast);
            }
        }

    }

}
