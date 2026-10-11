using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Regression tests for egglog's stale-row hole (map §8.1 row 1): its <c>scan_generic_bounded</c> evaluates
    /// constraints on raw rows without checking for tombstones (core-relations/src/table/mod.rs:429-439, 1018-1025), so
    /// a deleted, superseded or rebuilt row whose remaining cells satisfy a slow constraint reaches the join. Every path
    /// that yields rows must skip tombstones.
    /// </summary>
    public class StaleRowRegressionTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        static List<int> Scan(Table table, SubsetRef subset, params Constraint[] constraints)
        {
            var collector = new RowCollector();
            var next = 0;
            while (next >= 0)
                next = table.ScanGenericBounded(subset, next, 2, constraints, ref collector);

            return collector.Rows.Select(x => x.Id).ToList();
        }

        static List<int> Ids(Subset subset)
        {
            var ids = new List<int>();
            foreach (var row in subset)
                ids.Add(row);

            subset.Dispose();
            return ids;
        }

        [Fact]
        public void ConstrainedScanSkipsADeletedRow()
        {
            var state = TableTestUtil.EmptyState();
            var table = TableTestUtil.FillTable([R(1, 5, 5), R(2, 5, 6)], 1, null, ClosureMerge.TakeNew);
            TableTestUtil.StageRemove(table, R(1));
            table.Merge(state);

            // The deleted row [stale, 5, 5] satisfies c1 == c2; egglog would emit it.
            Assert.Empty(Scan(table, table.All().AsRef(), Constraint.Eq(C(1), C(2))));
            Assert.Empty(Scan(table, table.All().AsRef(), Constraint.EqConst(C(2), 5)));
            Assert.Equal(new[] { 1 }, Scan(table, table.All().AsRef(), Constraint.EqConst(C(1), 5)));
        }

        [Fact]
        public void ColumnZeroConstraintsNeverMatchTheTombstoneMarker()
        {
            var state = TableTestUtil.EmptyState();
            var table = TableTestUtil.FillTable([R(1, 1), R(2, 2)], 1, null, ClosureMerge.TakeNew);
            TableTestUtil.StageRemove(table, R(1));
            table.Merge(state);

            // The tombstone's first cell is uint.MaxValue, which is greater than anything.
            Assert.Equal(new[] { 1 }, Scan(table, table.All().AsRef(), Constraint.GtConst(C(0), 1)));
            Assert.Equal(new[] { 1 }, Scan(table, table.All().AsRef(), Constraint.GeConst(C(0), 0)));
            Assert.Empty(Scan(table, table.All().AsRef(), Constraint.EqConst(C(0), Value.StaleRep)));
        }

        [Fact]
        public void ConstrainedScanSkipsASupersededRow()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, new KeepIncomingMerge(C(1)), ParallelHeuristics.Serial);
            TableTestUtil.Stage(table, R(1, 7, 1));
            table.Merge(state);
            TableTestUtil.Stage(table, R(1, 8, 2));
            table.Merge(state);

            // :merge new superseded value 7; it must not be re-derivable from a scan.
            Assert.Empty(Scan(table, table.All().AsRef(), Constraint.EqConst(C(1), 7)));
            Assert.Equal(new[] { 1 }, Scan(table, table.All().AsRef(), Constraint.EqConst(C(1), 8)));

            // Fast subsets include the tombstone; reading through the table drops it.
            Assert.True(table.TryFastSubset(Constraint.LtConst(C(2), 2), out var old));
            Assert.Equal(1, old.Size);
            Assert.Empty(Scan(table, old.AsRef(), Constraint.EqConst(C(1), 7)));
            Assert.Empty(Scan(table, old.AsRef()));
            Assert.Equal(0, table.RefineLive(old).Size);
        }

        [Fact]
        public void EveryReadPathSkipsTombstones()
        {
            var state = TableTestUtil.EmptyState();
            var rows = Enumerable.Range(0, 12).Select(i => R((uint)i, (uint)(i % 3), (uint)(i % 3))).ToArray();
            var table = TableTestUtil.FillTable(rows, 1, null, ClosureMerge.TakeNew);
            TableTestUtil.StageRemove(table, R(0), R(4), R(8), R(9));
            table.Merge(state);
            var live = new HashSet<int> { 1, 2, 3, 5, 6, 7, 10, 11 };

            // Constrained and unconstrained, dense and sparse, chunked and whole.
            var sparse = Subset.Empty;
            for (var r = 0; r < 12; r++)
                sparse.AddRowSorted(r);

            foreach (var subset in new[] { table.All().AsRef(), sparse.AsRef() })
            {
                Assert.Equal(live.Order(), Scan(table, subset));
                Assert.Equal(live.Where(r => r % 3 == 1).Order(), Scan(table, subset, Constraint.Eq(C(1), C(2)), Constraint.EqConst(C(1), 1)));
                Assert.Equal(live.Where(r => r > 3).Order(), Scan(table, subset, Constraint.GtConst(C(0), 3)));
            }

            // Projections and per-column scans.
            var projected = new RowCollector();
            Table wrapped = table;
            wrapped.ScanProject(sparse.AsRef(), [C(2), C(1)], 0, int.MaxValue, [Constraint.Eq(C(1), C(2))], ref projected);
            Assert.Equal(live.Order(), projected.Rows.Select(x => x.Id));
            var column = new RowCollector();
            wrapped.ForEachColumn(table.All().AsRef(), C(1), ref column);
            Assert.Equal(live.Order(), column.Rows.Select(x => x.Id));

            // Refinement.
            Assert.Equal(live.Order(), Ids(table.RefineLive(table.All())));
            Assert.Equal(live.Order(), Ids(table.RefineOne(table.All(), Constraint.Eq(C(1), C(2)))));
            Assert.Equal(new[] { 3, 6 }, Ids(table.RefineOne(table.All(), Constraint.EqConst(C(1), 0))));
            Assert.Equal(new[] { 3, 6 }, Ids(table.Refine(sparse.Clone(), [Constraint.EqConst(C(2), 0), Constraint.Eq(C(1), C(2))])));

            // Point lookups and whole-table scans.
            Assert.Equal(-1, table.FindRow(4));
            Assert.Null(table.GetRow(R(9)));
            Assert.Equal(live.Order(), TableTestUtil.DumpBuffer(table.Scan(table.All().AsRef())).Select(x => x.Id));
            sparse.Dispose();
        }

        [Fact]
        public void RebuiltRowsLeaveNoMatchableTombstones()
        {
            var catalog = new TableCatalog();
            var uf = new DisplacedTable();
            var ufId = catalog.Add(uf);
            var table = new SortedWritesTable(1, 3, C(2), [C(0), C(1)], new UnionIdMerge(C(1), ufId, C(2)), ParallelHeuristics.Serial);
            catalog.Add(table);
            var state = new ExecutionState(catalog, 1);

            TableTestUtil.Stage(table, R(1, 10, 1), R(2, 20, 1));
            table.Merge(state);
            TableTestUtil.Stage(uf, R(10, 20, 2));
            uf.Merge(state);

            Assert.True(table.ApplyRebuild(ufId, uf, 3, state));
            table.Merge(state);

            // The old row [2, 20, 1] is a tombstone now: nothing may bind the non-canonical 20.
            Assert.Empty(Scan(table, table.All().AsRef(), Constraint.EqConst(C(1), 20)));
            Assert.Equal(2, Scan(table, table.All().AsRef(), Constraint.EqConst(C(1), 10)).Count);
        }

    }

}
