using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Serial-versus-parallel tests of <see cref="SortedWritesTable"/> above the 20000-row threshold: inserts with
    /// merges that change values, deletes, compaction and rebuild give the same contents on both paths, with the
    /// parallel insert storing the merge output (egglog drops it: core-relations/src/table/mod.rs:902-907) and
    /// reporting only real changes (egglog reports every staged row: mod.rs:871); and a parallel insert that fails
    /// leaves the table consistent.
    /// </summary>
    public class SortedWritesTableParallelTests
    {

        const int Large = 30_000;

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        static readonly ParallelHeuristics Parallel4 = new(4);

        static Dictionary<uint, (uint Value, uint Ts)> Contents(SortedWritesTable table)
        {
            var result = new Dictionary<uint, (uint, uint)>();
            foreach (var row in TableTestUtil.LiveRows(table))
                Assert.True(result.TryAdd(row[0], (row[1], row[2])), "duplicate key " + row[0]);

            return result;
        }

        static void StageAll(Table table, IEnumerable<uint[]> rows)
        {
            // Several buffers, as several rule threads would flush.
            var buffers = Enumerable.Range(0, 3).Select(_ => table.NewBuffer()).ToArray();
            var i = 0;
            foreach (var row in rows)
                buffers[i++ % buffers.Length].StageInsert(row);

            foreach (var buffer in buffers)
                buffer.Flush();
        }

        static void AssertSameContents(SortedWritesTable serial, SortedWritesTable parallel)
        {
            var expected = Contents(serial);
            var actual = Contents(parallel);
            Assert.Equal(expected.Count, actual.Count);
            foreach (var (key, value) in expected)
            {
                Assert.True(actual.TryGetValue(key, out var other), "missing key " + key);
                Assert.Equal(value, other);
            }
        }

        [Theory]
        [InlineData("min")]
        [InlineData("max")]
        [InlineData("sum")]
        [InlineData("union-id")]
        [InlineData("assert-equal")]
        public void ParallelInsertMatchesSerial(string mergeName)
        {
            var results = new List<(SortedWritesTable Table, DisplacedTable Uf, List<(TableChange Change, bool StateChanged)> Reports)>();
            foreach (var parallelism in new[] { ParallelHeuristics.Serial, Parallel4 })
            {
                var random = new Random(42);
                var catalog = new TableCatalog();
                var uf = new DisplacedTable();
                var ufId = catalog.Add(uf);
                MergeFunction merge = mergeName switch
                {
                    "min" => new MinMerge(C(1)),
                    "max" => new MaxMerge(C(1)),
                    "sum" => new SumMerge(1),
                    "union-id" => new UnionIdMerge(C(1), ufId, C(2)),
                    _ => new AssertEqualMerge(C(1)),
                };

                var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, merge, parallelism);
                catalog.Add(table);
                var reports = new List<(TableChange, bool)>();
                var valueDomain = mergeName switch { "sum" => 10, "union-id" => 5000, "assert-equal" => 3, _ => 1000 };

                // Fresh keys, then a batch with duplicates inside it and collisions with the table, then a smaller one.
                foreach (var (ts, rows, keys) in new[] { (1u, Large, Large), (2u, 2 * Large, Large + Large / 2), (3u, 25_000, Large) })
                {
                    StageAll(table, Enumerable.Range(0, rows).Select(i => R(ts == 1 ? (uint)i : (uint)random.Next(keys), (uint)random.Next(valueDomain), ts)));
                    var state = new ExecutionState(catalog, ts);
                    var change = table.Merge(state);
                    state.Flush();
                    uf.Merge(state);
                    reports.Add((change, state.Changed));
                }

                results.Add((table, uf, reports));
            }

            AssertSameContents(results[0].Table, results[1].Table);
            Assert.Equal(results[0].Reports, results[1].Reports);
            Assert.Equal(results[0].Uf.Count, results[1].Uf.Count);
            for (uint v = 0; v < 5000; v++)
                Assert.Equal(results[0].Uf.FindNaive(v), results[1].Uf.FindNaive(v));
        }

        [Fact]
        public void ParallelInsertStoresTheMergeOutput()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, new SumMerge(1), Parallel4);
            StageAll(table, Enumerable.Range(0, Large).Select(i => R((uint)i, 1, 1)));
            Assert.True(table.Merge(state).Added);

            // Each key gets +2 then +3 in one batch: the batch folds to 5, and merging it into 1 must store 6. egglog's
            // parallel insert stores the incoming (folded) row instead and leaves 5.
            var batch = table.NewBuffer();
            for (uint k = 0; k < Large; k++)
                batch.StageInsert(R(k, 2, 2));

            for (uint k = 0; k < Large; k++)
                batch.StageInsert(R(k, 3, 2));

            batch.Flush();
            Assert.True(table.Merge(state).Added);
            Assert.Equal(Large, table.Count);
            foreach (var (_, (value, ts)) in Contents(table))
                Assert.Equal((6u, 2u), (value, ts));
        }

        [Fact]
        public void ParallelInsertReportsOnlyRealChanges()
        {
            var state = TableTestUtil.EmptyState();
            var serial = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, new MinMerge(C(1)), ParallelHeuristics.Serial);
            var parallel = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, new MinMerge(C(1)), Parallel4);
            foreach (var table in new[] { serial, parallel })
            {
                StageAll(table, Enumerable.Range(0, Large).Select(i => R((uint)i, (uint)i, 1)));
                Assert.Equal(new TableChange(true, false), table.Merge(state));

                // Re-deriving existing facts (and larger values) changes nothing; egglog's parallel path reports a
                // change.
                StageAll(table, Enumerable.Range(0, 25_000).Select(i => R((uint)i, (uint)i + (uint)(i % 2), 2)));
                Assert.Equal(new TableChange(false, false), table.Merge(state));
                Assert.False(state.Changed);
            }

            AssertSameContents(serial, parallel);

            // The parallel path appends every staged row and tombstones the ones the merge rejected; the serial path
            // writes nothing. This is how the test knows the parallel path ran.
            Assert.Equal(Large, serial.Rows.Count);
            Assert.Equal(Large + 25_000, parallel.Rows.Count);
            Assert.Equal(25_000, parallel.StaleCount);
        }

        [Fact]
        public void ParallelInsertRequiresOneSortValuePerBatch()
        {
            var state = TableTestUtil.EmptyState();
            var serial = new SortedWritesTable(1, 2, C(1), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial);
            var parallel = new SortedWritesTable(1, 2, C(1), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, Parallel4);
            var rows = Enumerable.Range(0, Large).Select(i => R((uint)i, i < Large / 2 ? 1u : 2u)).ToArray();

            TableTestUtil.Stage(serial, rows);
            Assert.True(serial.Merge(state).Added);

            TableTestUtil.Stage(parallel, rows);
            Assert.ThrowsAny<Exception>(() => parallel.Merge(state));
        }

        [Fact]
        public void ParallelDeleteMatchesSerial()
        {
            var state = TableTestUtil.EmptyState();
            var tables = new[]
            {
                new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial),
                new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, Parallel4),
            };

            foreach (var table in tables)
            {
                StageAll(table, Enumerable.Range(0, 2 * Large).Select(i => R((uint)i, (uint)i * 3, 1)));
                table.Merge(state);

                // 25000 removals, a fifth of them for keys that do not exist.
                var removals = table.NewBuffer();
                for (var i = 0; i < 25_000; i++)
                    removals.StageRemove(R((uint)(i * 3 % (5 * Large / 2))));

                removals.Flush();
                Assert.Equal(new TableChange(false, true), table.Merge(state));
            }

            AssertSameContents(tables[0], tables[1]);
            Assert.Equal(tables[0].Count, tables[1].Count);
            Assert.Equal(tables[0].StaleCount, tables[1].StaleCount);
        }

        [Fact]
        public void ParallelRehashMatchesSerial()
        {
            var state = TableTestUtil.EmptyState();
            var tables = new[]
            {
                new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial),
                new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, Parallel4),
            };

            foreach (var table in tables)
            {
                for (uint ts = 1; ts <= 3; ts++)
                {
                    var first = (ts - 1) * 15_000;
                    StageAll(table, Enumerable.Range(0, 15_000).Select(i => R(first + (uint)i, (uint)i, ts)));
                    table.Merge(state);
                }

                // Delete two thirds, including all of timestamp 2: compaction runs, in parallel for the second table.
                var removals = table.NewBuffer();
                for (uint k = 0; k < 45_000; k++)
                {
                    if (k >= 15_000 && k < 30_000 || k % 2 == 0)
                        removals.StageRemove(R(k));
                }

                removals.Flush();
                table.Merge(state);
                Assert.Equal(1, table.Version.Major);
                Assert.Equal(0, table.StaleCount);
                Assert.Equal(table.Count, table.Rows.Count);
                Assert.Equal(2, table.SortRunCount);
            }

            AssertSameContents(tables[0], tables[1]);

            // The runs and fast subsets agree, though rows within a run are ordered by shard after a parallel
            // compaction.
            foreach (var ts in new uint[] { 0, 1, 2, 3, 4 })
            {
                var expected = Contents(tables[0]).Where(x => x.Value.Ts >= ts).Select(x => x.Key).Order().ToList();
                foreach (var table in tables)
                {
                    Assert.True(table.TryFastSubset(Constraint.GeConst(C(2), ts), out var subset));
                    Assert.Equal(expected, TableTestUtil.DumpSubset(table, subset.AsRef()).Select(x => x.Row[0]).Order().ToList());
                }
            }

            // Every key still finds its row.
            foreach (var table in tables)
                foreach (var (key, value) in Contents(tables[0]))
                    Assert.Equal(R(key, value.Value, value.Ts), table.GetRow(R(key))!.Value.Values);
        }

        [Fact]
        public void ParallelFullRebuildMatchesSerial()
        {
            var results = new List<SortedWritesTable>();
            foreach (var parallelism in new[] { ParallelHeuristics.Serial, Parallel4 })
            {
                var catalog = new TableCatalog();
                var uf = new DisplacedTable();
                var ufId = catalog.Add(uf);
                var table = new SortedWritesTable(1, 3, C(2), [C(0), C(1)], new UnionIdMerge(C(1), ufId, C(2)), parallelism);
                catalog.Add(table);
                var state = new ExecutionState(catalog, 1);

                // f(k) = k + 100000 for 30000 keys, then union every pair (2j, 2j + 1) of keys and of values.
                StageAll(table, Enumerable.Range(0, Large).Select(i => R((uint)i, (uint)i + 100_000, 1)));
                table.Merge(state);
                var unions = uf.NewBuffer();
                for (uint j = 0; j < Large / 2; j++)
                {
                    unions.StageInsert(R(2 * j, 2 * j + 1, 2));
                    unions.StageInsert(R(2 * j + 100_000, 2 * j + 100_001, 2));
                }

                unions.Flush();
                uf.Merge(state);

                // Rebuild until nothing changes, as the bridge's rebuild loop does.
                uint ts = 3;
                while (table.ApplyRebuild(ufId, uf, ts, state))
                {
                    table.Merge(state);
                    state.Flush();
                    uf.Merge(state);
                    ts++;
                }

                Assert.Equal(Large / 2, table.Count);
                foreach (var (key, (value, _)) in Contents(table))
                {
                    Assert.Equal(key, uf.FindNaive(key));
                    Assert.Equal(value, uf.FindNaive(value));
                    Assert.Equal(key + 100_000, value);
                }

                results.Add(table);
            }

            AssertSameContents(results[0], results[1]);
        }

        static int ShardOf(SortedWritesTable table, uint key) => table.Parallelism.ShardData.ShardOf(TableKeyHash.Hash(R(key), 1));

        /// <summary>
        /// <c>ParallelInsert</c> hands the table's row buffer to a <see cref="ParallelRowBufferWriter"/>
        /// (<c>data.ParallelWriter()</c> empties the buffer object) and puts the rows back with <c>data =
        /// writer.Finish()</c> in a <c>finally</c>, so an exception inside a shard task (here the sort-order check;
        /// equally a merge function that throws) does not leave the table with an empty row buffer while its key index
        /// still names thousands of rows, which later point lookups would read past through the unchecked
        /// <c>Key*Match</c> structs. The serial path stays consistent after the same misuse.
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
        /// Each shard sees one sort value but the shards disagree. <c>ParallelInsert</c> checks the shards against each
        /// other before any write and throws with the table untouched; checked only after every row was appended,
        /// merged and indexed, the mismatch would leave an inexact stale count (zero here, so scans would take the
        /// no-tombstone fast path and emit tombstones) and rows that no timestamp fast subset can see.
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
            // timestamp 2, which would tombstone their old rows. No shard's own check fails; the shards disagree with
            // each other.
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
