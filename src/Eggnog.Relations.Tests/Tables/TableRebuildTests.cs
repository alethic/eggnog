using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for rebuilding a <see cref="SortedWritesTable"/> against the union-find (<see cref="Table.ApplyRebuild"/>,
    /// egglog's core-relations/src/table/rebuild.rs), <see cref="Table.RefreshRowsForValues"/> and
    /// <see cref="SubsetTracker"/>.
    /// </summary>
    public class TableRebuildTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        /// <summary>
        /// A catalog with the union-find at id 0 and a function table <c>f(a) = r</c> at id 1, with columns
        /// <c>[a, r, ts]</c> sorted by timestamp, both id columns rebuildable, and a union-id merge.
        /// </summary>
        static (TableCatalog Catalog, DisplacedTable Uf, TableId UfId, SortedWritesTable Table) Setup(ParallelHeuristics? parallelism = null)
        {
            var catalog = new TableCatalog();
            var uf = new DisplacedTable();
            var ufId = catalog.Add(uf);
            var table = new SortedWritesTable(1, 3, C(2), [C(0), C(1)], new UnionIdMerge(C(1), ufId, C(2)), parallelism ?? ParallelHeuristics.Serial);
            catalog.Add(table);
            return (catalog, uf, ufId, table);
        }

        static Dictionary<uint, uint[]> ByKey(SortedWritesTable table) => TableTestUtil.LiveRows(table).ToDictionary(r => r[0]);

        [Fact]
        public void RebuildCanonicalizesRowsAndMergesCollisions()
        {
            var (catalog, uf, ufId, table) = Setup();
            var state = new ExecutionState(catalog, 1);
            TableTestUtil.Stage(table, R(1, 10, 1), R(2, 20, 1), R(3, 30, 1));
            table.Merge(state);

            TableTestUtil.Stage(uf, R(1, 2, 2));
            uf.Merge(state);

            Assert.True(table.ApplyRebuild(ufId, uf, 3, state));
            Assert.True(table.Merge(state).Removed);

            // f(2) = 20 became f(1) = 20, which collided with f(1) = 10: the union-id merge kept 10 and staged 10 = 20.
            var rows = ByKey(table);
            Assert.Equal(2, rows.Count);
            Assert.Equal(R(1, 10, 1), rows[1]);
            Assert.Equal(R(3, 30, 1), rows[3]);
            Assert.True(state.Changed);
            state.Flush();
            Assert.True(uf.Merge(state).Added);
            Assert.Equal(10u, uf.FindNaive(20));

            // Nothing is left to canonicalize.
            Assert.False(table.ApplyRebuild(ufId, uf, 4, state));
        }

        [Fact]
        public void RebuiltRowsCarryTheNextTimestamp()
        {
            var (catalog, uf, ufId, table) = Setup();
            var state = new ExecutionState(catalog, 1);
            TableTestUtil.Stage(table, R(1, 10, 1), R(2, 11, 1));
            table.Merge(state);
            TableTestUtil.Stage(uf, R(11, 12, 2), R(11, 10, 2));
            uf.Merge(state);

            Assert.True(table.ApplyRebuild(ufId, uf, 7, state));
            table.Merge(state);
            var rows = ByKey(table);
            Assert.Equal(R(1, 10, 1), rows[1]);
            Assert.Equal(R(2, 10, 7), rows[2]);

            // The rewritten row is a delta for timestamp 7.
            Assert.True(table.TryFastSubset(Constraint.GeConst(C(2), 7), out var delta));
            Assert.Equal(new[] { 2u }, TableTestUtil.DumpSubset(table, delta.AsRef()).Select(x => x.Row[0]));
        }

        [Fact]
        public void TablesWithoutRebuildColumnsOrRebuildersDoNothing()
        {
            var catalog = new TableCatalog();
            var uf = new DisplacedTable();
            var ufId = catalog.Add(uf);
            var state = new ExecutionState(catalog);
            var plain = TableTestUtil.FillTable([R(1, 2)], 1, null, ClosureMerge.TakeNew);
            TableTestUtil.Stage(uf, R(1, 2, 0));
            uf.Merge(state);
            Assert.False(plain.ApplyRebuild(ufId, uf, 1, state));

            // A sorted-writes table provides no rebuilder.
            var (_, _, _, table) = Setup();
            TableTestUtil.Stage(table, R(2, 2, 0));
            table.Merge(state);
            Assert.Null(plain.GetRebuilder([C(0)]));
            Assert.False(table.ApplyRebuild(new TableId(5), plain, 1, state));
            Assert.False(plain.RefreshRowsForValues([1], 1));
        }

        [Fact]
        public void IncrementallyEligibleRebuildsFallBackToTheFullPath()
        {
            Assert.True(SortedWritesTable.ShouldRebuildIncrementally(10, 20_000, false));
            Assert.False(SortedWritesTable.ShouldRebuildIncrementally(10, 10_000, false));
            Assert.False(SortedWritesTable.ShouldRebuildIncrementally(2_501, 20_000, false));
            Assert.True(SortedWritesTable.ShouldRebuildIncrementally(2_500, 20_000, false));
            Assert.False(SortedWritesTable.ShouldRebuildIncrementally(3, 20_000, true));
            Assert.True(SortedWritesTable.ShouldRebuildIncrementally(2, 20_000, true));

            // 12000 rows and a delta of two unions: the incremental heuristic applies, and without the rebuild index the
            // full path gives the right answer.
            var (catalog, uf, ufId, table) = Setup();
            var state = new ExecutionState(catalog, 1);
            TableTestUtil.Stage(table, Enumerable.Range(0, 12_000).Select(i => R((uint)i, (uint)i + 50_000, 1)).ToArray());
            table.Merge(state);
            Assert.True(table.ApplyRebuild(ufId, uf, 2, state) == false);

            TableTestUtil.Stage(uf, R(100, 101, 3), R(50_200, 50_201, 3));
            uf.Merge(state);
            Assert.True(table.ApplyRebuild(ufId, uf, 4, state));
            table.Merge(state);

            var rows = ByKey(table);
            Assert.Equal(11_999, rows.Count);
            Assert.False(rows.ContainsKey(101));
            Assert.Equal(R(100, 50_100, 1), rows[100]);
            Assert.Equal(R(200, 50_200, 1), rows[200]);
            Assert.Equal(R(201, 50_200, 4), rows[201]);
        }

        [Fact]
        public void RefreshRowsForValuesRestampsRowsMentioningDirtyIds()
        {
            var (catalog, _, _, table) = Setup();
            var state = new ExecutionState(catalog, 1);
            TableTestUtil.Stage(table, R(1, 10, 1), R(2, 20, 1), R(3, 1, 1), R(4, 40, 1));
            table.Merge(state);
            TableTestUtil.StageRemove(table, R(4));
            table.Merge(state);

            Assert.False(table.RefreshRowsForValues(ReadOnlySpan<uint>.Empty, 5));
            Assert.False(table.RefreshRowsForValues([99, 40], 5));
            Assert.True(table.RefreshRowsForValues([1, 20], 5));
            var change = table.Merge(state);
            Assert.Equal(new TableChange(true, true), change);

            // Rows mentioning 1 (as key or value) and 20 moved to the end with the new timestamp; the others did not.
            var rows = ByKey(table);
            Assert.Equal(3, rows.Count);
            Assert.Equal(R(1, 10, 5), rows[1]);
            Assert.Equal(R(2, 20, 5), rows[2]);
            Assert.Equal(R(3, 1, 5), rows[3]);
            Assert.True(table.TryFastSubset(Constraint.GeConst(C(2), 5), out var delta));
            Assert.Equal(3, TableTestUtil.DumpSubset(table, delta.AsRef()).Count);
        }

        [Fact]
        public void SubsetTrackerReturnsTheUnseenSuffixUntilTheGenerationChanges()
        {
            var state = TableTestUtil.EmptyState();
            var uf = new DisplacedTable();
            var id = new TableId(3);
            var tracker = new SubsetTracker();
            Assert.Equal(Subset.Dense(0, 0), tracker.RecentUpdates(id, uf));

            TableTestUtil.Stage(uf, R(1, 2, 1), R(3, 4, 1));
            uf.Merge(state);
            Assert.Equal(Subset.Dense(0, 2), tracker.RecentUpdates(id, uf));
            Assert.Equal(Subset.Dense(2, 2), tracker.RecentUpdates(id, uf));

            TableTestUtil.Stage(uf, R(5, 6, 2));
            uf.Merge(state);
            var clone = tracker.Clone();
            Assert.Equal(Subset.Dense(2, 3), tracker.RecentUpdates(id, uf));
            Assert.Equal(Subset.Dense(2, 3), clone.RecentUpdates(id, uf));

            // Another provider is tracked separately.
            Assert.Equal(Subset.Dense(0, 3), tracker.RecentUpdates(new TableId(4), uf));

            // Clearing the union-find starts a new generation: the next call returns everything.
            uf.Clear();
            TableTestUtil.Stage(uf, R(7, 8, 1));
            uf.Merge(state);
            Assert.Equal(Subset.Dense(0, 1), tracker.RecentUpdates(id, uf));

            tracker.Reset();
            Assert.Equal(Subset.Dense(0, 1), tracker.RecentUpdates(id, uf));
        }

        [Fact]
        public void RebuildLoopReachesAFixpoint()
        {
            // Congruence closure on f: f(1) = 10, f(2) = 20, f(10) = 100, f(20) = 200. Unioning 1 and 2 makes f(1) = f(2),
            // so 10 = 20, so f(10) = f(20), so 100 = 200.
            foreach (var parallelism in new[] { ParallelHeuristics.Serial, new ParallelHeuristics(4) })
            {
                var (catalog, uf, ufId, table) = Setup(parallelism);
                var state = new ExecutionState(catalog, 1);
                TableTestUtil.Stage(table, R(1, 10, 1), R(2, 20, 1), R(10, 100, 1), R(20, 200, 1));
                table.Merge(state);
                TableTestUtil.Stage(uf, R(1, 2, 2));
                uf.Merge(state);

                uint ts = 3;
                var rounds = 0;
                while (table.ApplyRebuild(ufId, uf, ts, state))
                {
                    table.Merge(state);
                    state.Flush();
                    uf.Merge(state);
                    ts++;
                    rounds++;
                    Assert.True(rounds < 10);
                }

                Assert.Equal(1u, uf.FindNaive(2));
                Assert.Equal(10u, uf.FindNaive(20));
                Assert.Equal(100u, uf.FindNaive(200));
                var rows = ByKey(table);
                Assert.Equal(2, rows.Count);
                Assert.Equal(10u, rows[1][1]);
                Assert.Equal(100u, rows[10][1]);
            }
        }

    }

}
