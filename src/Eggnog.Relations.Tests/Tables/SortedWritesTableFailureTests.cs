using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Failure paths of <see cref="SortedWritesTable"/>'s inserts, and parallel inserts of shards larger than one batch.
    /// egglog panics with the table half-written when a sort check or a merge function fails
    /// (core-relations/src/table/mod.rs:699-974); here the exception propagates, the table's rows, key index, stale
    /// count and sort runs still agree, and the staged inserts not applied are dropped.
    /// </summary>
    public class SortedWritesTableFailureTests
    {

        const int Large = 30_000;

        static readonly ParallelHeuristics Parallel4 = new(4);

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        static void StageAll(Table table, IEnumerable<uint[]> rows)
        {
            var buffers = Enumerable.Range(0, 3).Select(_ => table.NewBuffer()).ToArray();
            var i = 0;
            foreach (var row in rows)
                buffers[i++ % buffers.Length].StageInsert(row);

            foreach (var buffer in buffers)
                buffer.Flush();
        }

        static ClosureMerge FailingMerge(Func<bool> fail, Func<uint[], bool> failsOn)
        {
            return new ClosureMerge((_, incoming) => fail() && failsOn(incoming) ? throw new InvalidOperationException("The merge function failed.") : incoming);
        }

        static void AssertMergeFailed(Exception exception)
        {
            var all = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToList() : new List<Exception> { exception };
            Assert.Contains(all, e => e.Message == "The merge function failed.");
        }

        /// <summary>
        /// Checks the table's invariants: every live row is the one its key's index entry names, the stale count and
        /// the live count match the rows, the sort column is nondecreasing over live rows, and the fast subset of every
        /// sort value agrees with a filtered scan.
        /// </summary>
        static void AssertConsistent(SortedWritesTable table)
        {
            var rows = table.Rows;
            var sort = table.SortColumn;
            var tombstones = 0;
            var live = 0;
            var last = 0u;
            for (var r = 0; r < rows.Count; r++)
            {
                if (rows.IsStale(r))
                {
                    tombstones++;
                    continue;
                }

                live++;
                var row = rows.GetRow(r);
                Assert.Equal(r, table.FindRow(row[0]));
                if (sort >= 0)
                {
                    Assert.True(row[sort] >= last, $"Row {r} breaks the sort order.");
                    last = row[sort];
                }
            }

            Assert.Equal(tombstones, table.StaleCount);
            Assert.Equal(live, table.Count);
            if (sort < 0)
                return;

            var liveRows = TableTestUtil.LiveRows(table);
            Assert.DoesNotContain(liveRows, row => row[0] == Value.StaleRep);
            foreach (var value in liveRows.Select(row => row[sort]).Distinct().Append(last + 1))
            {
                Assert.True(table.TryFastSubset(Constraint.GeConst(C(sort), value), out var fast));
                Assert.Equal(liveRows.Count(row => row[sort] >= value), TableTestUtil.DumpSubset(table, fast.AsRef()).Count);
            }
        }

        [Fact]
        public void SerialFailuresLeaveTheTableConsistent()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, FailingMerge(() => true, row => row[0] == 50), ParallelHeuristics.Serial);
            TableTestUtil.Stage(table, Enumerable.Range(0, 100).Select(i => R((uint)i, (uint)i, 1)).ToArray());
            table.Merge(state);

            TableTestUtil.Stage(table, Enumerable.Range(0, 110).Select(i => R((uint)i, (uint)i + 1, 2)).ToArray());
            AssertMergeFailed(Assert.ThrowsAny<Exception>(() => table.Merge(state)));
            AssertConsistent(table);
            Assert.Equal(R(50, 50, 1), table.GetRow(R(50))!.Value.Values);

            // What the failed merge did not apply is gone.
            var rows = table.Rows.Count;
            Assert.Equal(new TableChange(false, false), table.Merge(state));
            Assert.Equal(rows, table.Rows.Count);

            // A row below the last sort value throws before it is written.
            TableTestUtil.Stage(table, R(200, 200, 1));
            Assert.Throws<InvalidOperationException>(() => table.Merge(state));
            Assert.Null(table.GetRow(R(200)));
            AssertConsistent(table);
        }

        [Fact]
        public void SortViolationInOneShardWritesNothing()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, Parallel4);
            StageAll(table, Enumerable.Range(0, Large).Select(i => R((uint)i, (uint)i, 2)));
            table.Merge(state);

            // Every row but one is fine; the one below the last sort value fails the whole parallel merge up front.
            StageAll(table, Enumerable.Range(Large, Large).Select(i => R((uint)i, (uint)i, i == Large + 7 ? 1u : 3u)));
            var exception = Assert.Throws<InvalidOperationException>(() => table.Merge(state));
            Assert.Contains("sort", exception.Message);
            Assert.Equal(Large, table.Rows.Count);
            Assert.Equal(1, table.SortRunCount);
            AssertConsistent(table);

            // The failed batch was consumed; the next one applies normally.
            StageAll(table, Enumerable.Range(Large, Large).Select(i => R((uint)i, (uint)i, 3)));
            Assert.True(table.Merge(state).Added);
            Assert.Equal(2 * Large, table.Count);
            AssertConsistent(table);
        }

        [Fact]
        public void MergeFunctionThrowingWhileFoldingWritesNothing()
        {
            var state = TableTestUtil.EmptyState();
            var fail = true;
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, FailingMerge(() => fail, row => row[0] == Large + 5), Parallel4);
            StageAll(table, Enumerable.Range(0, Large).Select(i => R((uint)i, (uint)i, 1)));
            table.Merge(state);

            // New keys only, one of them twice: the batch's fold calls the merge function on that key, which throws
            // before any shard has written to the table.
            StageAll(table, Enumerable.Range(Large, Large).Select(i => R((uint)i, (uint)i, 2)).Append(R(Large + 5, 999, 2)));
            AssertMergeFailed(Assert.ThrowsAny<Exception>(() => table.Merge(state)));
            Assert.Equal(Large, table.Rows.Count);
            Assert.Equal(Large, table.Count);
            AssertConsistent(table);

            fail = false;
            StageAll(table, Enumerable.Range(Large, Large).Select(i => R((uint)i, (uint)i, 2)));
            table.Merge(state);
            Assert.Equal(2 * Large, table.Count);
            AssertConsistent(table);
            for (uint k = 0; k < 2 * Large; k++)
                Assert.Equal(R(k, k, k < Large ? 1u : 2u), table.GetRow(R(k))!.Value.Values);
        }

        [Fact]
        public void MergeFunctionThrowingWhileMergingLeavesTheTableConsistent()
        {
            var state = TableTestUtil.EmptyState();
            var fail = true;
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, FailingMerge(() => fail, row => row[0] == 12_345), Parallel4);
            StageAll(table, Enumerable.Range(0, Large).Select(i => R((uint)i, (uint)i, 1)));
            table.Merge(state);

            // Each key once, so the fold never calls the merge function; merging into the table does, and throws for
            // one key after its shard appended its rows.
            StageAll(table, Enumerable.Range(0, Large + 5_000).Select(i => R((uint)i, (uint)i + (i < Large ? 1u : 0u), 2)));
            AssertMergeFailed(Assert.ThrowsAny<Exception>(() => table.Merge(state)));
            Assert.True(table.Rows.Count > Large, "The failing shard appended its rows before throwing.");
            AssertConsistent(table);
            var physical = table.Rows.Count;
            Assert.Equal(new TableChange(false, false), table.Merge(state));
            Assert.Equal(physical, table.Rows.Count);

            // Every old key survives, in its old or its new version; the failing key keeps its old row.
            Assert.Equal(R(12_345, 12_345, 1), table.GetRow(R(12_345))!.Value.Values);
            for (uint k = 0; k < Large; k++)
            {
                var row = table.GetRow(R(k))!.Value.Values;
                Assert.True(row.SequenceEqual(R(k, k, 1)) || row.SequenceEqual(R(k, k + 1, 2)), TableTestUtil.Show(row));
            }

            for (uint k = Large; k < Large + 5_000; k++)
            {
                if (table.GetRow(R(k)) is { } row)
                    Assert.Equal(R(k, k, 2), row.Values);
            }

            // Compaction and the next merge work from that state.
            table.Rehash();
            AssertConsistent(table);
            fail = false;
            StageAll(table, Enumerable.Range(0, Large + 5_000).Select(i => R((uint)i, (uint)i + (i < Large ? 1u : 0u), 2)));
            table.Merge(state);
            Assert.Equal(Large + 5_000, table.Count);
            AssertConsistent(table);
            for (uint k = 0; k < Large + 5_000; k++)
                Assert.Equal(R(k, k + (k < Large ? 1u : 0u), 2), table.GetRow(R(k))!.Value.Values);
        }

        [Fact]
        public void LargeShardsFoldInSeveralBatches()
        {
            var state = TableTestUtil.EmptyState();
            var parallelism = new ParallelHeuristics(2);
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, new SumMerge(1), parallelism);
            var keys = parallelism.ShardData.Count * SortedWritesTable.ParallelInsertBatchSize + 100_000;
            var duplicates = 50_000;

            // Precondition: some shard gets more keys than one batch holds.
            var perShard = new int[parallelism.ShardData.Count];
            for (uint k = 0; k < keys; k++)
                perShard[parallelism.ShardData.ShardOf(TableKeyHash.Hash(R(k), 1))]++;

            Assert.True(perShard.Max() > SortedWritesTable.ParallelInsertBatchSize);

            // Every key once, then the first keys again: their second rows land in a later batch of their shard and
            // merge into the rows an earlier batch of the same insert wrote.
            var buffer = table.NewBuffer();
            var row = new uint[3];
            for (uint k = 0; k < keys; k++)
            {
                row[0] = k;
                row[1] = 1;
                row[2] = 1;
                buffer.StageInsert(row);
            }

            for (uint k = 0; k < duplicates; k++)
            {
                row[0] = k;
                row[1] = 1;
                row[2] = 1;
                buffer.StageInsert(row);
            }

            buffer.Flush();
            Assert.Equal(new TableChange(true, false), table.Merge(state));

            Assert.Equal(keys, table.Count);
            Assert.Equal(duplicates, table.StaleCount);
            Assert.Equal(keys + duplicates, table.Rows.Count);
            Assert.Equal(1, table.SortRunCount);
            var tombstones = 0;
            for (var r = 0; r < table.Rows.Count; r++)
            {
                if (table.Rows.IsStale(r))
                    tombstones++;
            }

            Assert.Equal(duplicates, tombstones);
            for (uint k = 0; k < keys; k++)
            {
                Assert.True(table.TryGetColumn(k, 1, out var value));
                Assert.Equal(k < duplicates ? 2u : 1u, value);
            }
        }

    }

}
