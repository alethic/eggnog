using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="OverlayDisplacedTable"/>, a child context's union-find as a table: the merge through the
    /// parent's find with its order check, journal rows with a computed canonical column, view-level point lookups,
    /// fast subsets and refinement, the base-class scan dispatch, statistics, freezing, and the refused clear and
    /// clone.
    /// </summary>
    public class OverlayDisplacedTableTests
    {

        static ColumnId C(int column) => new(column);

        /// <summary>
        /// A root union-find with 4 → 2 and 9 → 7 (at timestamp 1), and a child's table over it.
        /// </summary>
        static (DisplacedTable Root, OverlayDisplacedTable Child) Setup(IdBlockAllocator? localIds = null)
        {
            var root = new DisplacedTable();
            TableTestUtil.Stage(root, [4, 2, 1], [9, 7, 1]);
            root.Merge(TableTestUtil.EmptyState());
            var child = new OverlayDisplacedTable(new LayeredUnionFind(root.UnionFind), new UnionFindOverlay(), new DisplacedJournal(), root, localIds);
            return (root, child);
        }

        static void Union(OverlayDisplacedTable table, uint a, uint b, uint ts)
        {
            TableTestUtil.Stage(table, [a, b, ts]);
            table.Merge(TableTestUtil.EmptyState());
        }

        [Fact]
        public void TheTableHasTheUnionFindsShape()
        {
            var (_, child) = Setup();
            Assert.Equal(TableKind.OverlayDisplaced, child.Kind);
            Assert.Equal(3, child.Arity);
            Assert.Same(DisplacedTable.DisplacedSpec, child.Spec);
            Assert.Equal(0, child.Count);
            Assert.Equal(new TableVersion(0, 0), child.Version);
            Assert.False(child.HasStaleRows);
            Assert.Equal(0, child.All().Size);
            var rebuilder = child.GetRebuilder([C(0)]);
            Assert.IsType<OverlayCanonicalizer>(rebuilder);
            Assert.Equal(C(0), rebuilder!.HintColumn);
            Assert.Throws<ArgumentException>(() => new OverlayDisplacedTable(child.ParentFinder, new UnionFindOverlay(), new DisplacedJournal(), new SortedWritesTable(1, 2, null, [], new KeepCurrentMerge())));
        }

        [Fact]
        public void UnionsMapThroughTheParentAndJournalTheLoser()
        {
            var (root, child) = Setup();

            // 9 and 4 are 7 and 2 in the parent: the class {7, 2} forms, 7 loses.
            TableTestUtil.Stage(child, [9, 4, 3]);
            var change = child.Merge(TableTestUtil.EmptyState());
            Assert.Equal(new TableChange(true, true), change);
            Assert.Equal(1, child.Count);
            Assert.Equal(new TableVersion(0, 1), child.Version);
            Assert.Equal(new uint[] { 7 }, child.Journal.Ids.ToArray());
            Assert.Equal(2u, child.FindNaive(9));
            Assert.Equal(2u, child.FindNaive(7));
            Assert.Equal(2u, child.Finder.Find(4));
            Assert.Equal(1L, child.OwnUnions);

            // The parent is untouched.
            Assert.Equal(7u, root.FindNaive(9));
            Assert.Equal(2, root.Count);

            // A redundant union changes nothing and may carry an old timestamp.
            TableTestUtil.Stage(child, [7, 2, 0]);
            Assert.Equal(new TableChange(false, false), child.Merge(TableTestUtil.EmptyState()));
            Assert.Equal(1, child.Count);
        }

        [Fact]
        public void AnEffectiveUnionGoingBackInTimeThrowsBeforeChangingAnything()
        {
            var (_, child) = Setup();
            Union(child, 9, 4, 5);
            TableTestUtil.Stage(child, [20, 21, 4]);
            Assert.Throws<InvalidOperationException>(() => child.Merge(TableTestUtil.EmptyState()));
            Assert.Equal(1, child.Count);
            Assert.Equal(20u, child.FindNaive(20));
            Assert.Equal(21u, child.FindNaive(21));
        }

        [Fact]
        public void IdsBeyondTheUnionFindMaximumAreRefused()
        {
            var (_, child) = Setup();
            TableTestUtil.Stage(child, [Value.StaleRep, 1, 1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => child.Merge(TableTestUtil.EmptyState()));
        }

        [Fact]
        public void PointLookupsAreViewLevel()
        {
            var (_, child) = Setup();
            Union(child, 9, 4, 3);
            Span<uint> row = stackalloc uint[3];

            // A journaled id: its own row.
            Assert.True(child.TryGetRow([7], row, out var rowId));
            Assert.Equal(0, rowId);
            Assert.Equal(new uint[] { 7, 2, 3 }, row.ToArray());

            // An id displaced in the parent: the parent's row, with the child's canonical, and no row of this table.
            Assert.True(child.TryGetRow([9], row, out rowId));
            Assert.Equal(-1, rowId);
            Assert.Equal(new uint[] { 9, 2, 1 }, row.ToArray());

            // A canonical id has no row; column 1 answers for any id.
            Assert.False(child.TryGetRow([2], row, out rowId));
            Assert.Equal(-1, rowId);
            Assert.True(child.TryGetRowColumn([2], C(1), out var canonical));
            Assert.Equal(2u, canonical);
            Assert.True(child.TryGetColumn(1000, 1, out canonical));
            Assert.Equal(1000u, canonical);
            Assert.True(child.TryGetColumn(9, 1, out canonical));
            Assert.Equal(2u, canonical);

            // Columns 0 and 2, from the journal and from the parent.
            Assert.True(child.TryGetColumn(7, 2, out var ts));
            Assert.Equal(3u, ts);
            Assert.True(child.TryGetColumn(4, 2, out ts));
            Assert.Equal(1u, ts);
            Assert.True(child.TryGetColumn(4, 0, out var id));
            Assert.Equal(4u, id);
            Assert.False(child.TryGetColumn(2, 0, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => child.TryGetColumn(2, 3, out _));
            Assert.Throws<ArgumentException>(() => child.TryGetRow([1, 2], new uint[3], out _));
            Assert.Throws<ArgumentException>(() => child.TryGetRow([1], new uint[2], out _));
        }

        [Fact]
        public void AGrandchildLooksUpThroughEveryLevel()
        {
            var (root, child) = Setup();
            Union(child, 9, 4, 3);
            var grandchild = new OverlayDisplacedTable(child.Finder, new UnionFindOverlay(), new DisplacedJournal(), child);
            Union(grandchild, 2, 1, 5);
            Span<uint> row = stackalloc uint[3];

            Assert.True(grandchild.TryGetRow([2], row, out var rowId));
            Assert.Equal((0, 1u, 5u), (rowId, row[1], row[2]));
            Assert.True(grandchild.TryGetRow([7], row, out rowId));
            Assert.Equal((-1, 1u, 3u), (rowId, row[1], row[2]));
            Assert.True(grandchild.TryGetRow([4], row, out rowId));
            Assert.Equal((-1, 1u, 1u), (rowId, row[1], row[2]));
            Assert.Equal(1u, grandchild.FindNaive(9));
            Assert.Equal(2u, child.FindNaive(9));
            Assert.Equal(7u, root.FindNaive(9));
        }

        [Fact]
        public void FastSubsetsAndRefinementCoverTheJournal()
        {
            var (_, child) = Setup();
            Union(child, 20, 21, 3);
            Union(child, 30, 31, 3);
            Union(child, 21, 30, 5);
            Assert.Equal(new uint[] { 21, 31, 30 }, child.Journal.Ids.ToArray());

            Assert.True(child.TryFastSubset(Constraint.EqConst(C(0), 31), out var subset));
            Assert.Equal(new[] { 1 }, IndexTestUtil.ToArray(subset.AsRef()));
            Assert.True(child.TryFastSubset(Constraint.EqConst(C(0), 2), out subset));
            Assert.Equal(0, subset.Size);
            Assert.True(child.TryFastSubset(Constraint.EqConst(C(2), 3), out subset));
            Assert.Equal(new[] { 0, 1 }, IndexTestUtil.ToArray(subset.AsRef()));
            Assert.False(child.TryFastSubset(Constraint.EqConst(C(2), 4), out _));
            Assert.True(child.TryFastSubset(Constraint.LtConst(C(2), 5), out subset));
            Assert.Equal(new[] { 0, 1 }, IndexTestUtil.ToArray(subset.AsRef()));
            Assert.True(child.TryFastSubset(Constraint.GtConst(C(2), 3), out subset));
            Assert.Equal(new[] { 2 }, IndexTestUtil.ToArray(subset.AsRef()));
            Assert.True(child.TryFastSubset(Constraint.LeConst(C(2), 3), out subset));
            Assert.Equal(new[] { 0, 1 }, IndexTestUtil.ToArray(subset.AsRef()));
            Assert.True(child.TryFastSubset(Constraint.GeConst(C(2), 4), out subset));
            Assert.Equal(new[] { 2 }, IndexTestUtil.ToArray(subset.AsRef()));
            Assert.False(child.TryFastSubset(Constraint.EqConst(C(1), 20), out _));
            Assert.False(child.TryFastSubset(Constraint.Eq(C(0), C(1)), out _));

            // Column 1 is computed: every row's canonical is now 20.
            var refined = child.RefineOne(child.All(), Constraint.EqConst(C(1), 20));
            Assert.Equal(new[] { 0, 1, 2 }, IndexTestUtil.ToArray(refined.AsRef()));
            refined = child.Refine(child.All(), [Constraint.GtConst(C(0), 21), Constraint.Eq(C(1), C(1))]);
            Assert.Equal(new[] { 1, 2 }, IndexTestUtil.ToArray(refined.AsRef()));
            Assert.Throws<ArgumentOutOfRangeException>(() => child.RefineOne(child.All(), Constraint.EqConst(C(3), 1)));
        }

        [Fact]
        public void TheBaseScanDispatchesToTheJournal()
        {
            var (_, child) = Setup();
            Union(child, 20, 21, 3);
            Union(child, 9, 22, 4);
            var rows = TableTestUtil.DumpSubset(child, child.All().AsRef());
            Assert.Equal(new[] { 0, 1 }, rows.Select(r => r.Id).ToArray());
            Assert.Equal(new uint[] { 21, 20, 3 }, rows[0].Row);
            Assert.Equal(new uint[] { 22, 7, 4 }, rows[1].Row);

            var sink = new RowCollector();
            Table table = child;
            var next = table.ScanGenericBounded(child.All().AsRef(), 0, 1, [Constraint.GeConst(C(2), 0)], ref sink);
            Assert.Equal(1, next);
            Assert.Single(sink.Rows);
            var updates = TableTestUtil.DumpSubset(child, child.UpdatesSince(1).AsRef());
            Assert.Single(updates);
            Assert.Throws<ArgumentOutOfRangeException>(() => child.UpdatesSince(3));

            // ForEachColumn reads the hint column through the same dispatch, as an incremental rebuild does.
            var ids = new RowCollector();
            child.ForEachColumn(child.All().AsRef(), C(0), ref ids);
            Assert.Equal(new uint[] { 21, 22 }, ids.Rows.Select(r => r.Row[0]).ToArray());
        }

        [Fact]
        public void ParentIdsBeatenByLocalIdsAreCounted()
        {
            // Local ids start at 100; the parent's ids here are above them.
            var counters = new CounterSet();
            var ids = counters.Add();
            counters.IncrementBy(ids, 100);
            var blocks = new IdBlockAllocator(counters, ids);
            var (_, child) = Setup(blocks);
            var local = blocks.Next();
            Assert.Equal(100u, local);
            Union(child, 1000, local, 1);
            Union(child, 1001, 1002, 2);
            Assert.Equal(2L, child.OwnUnions);
            Assert.Equal(1L, child.ParentIdLosers);
        }

        [Fact]
        public void FreezeSealsTheTable()
        {
            var (_, child) = Setup();
            Union(child, 9, 4, 3);
            TableTestUtil.Stage(child, [20, 21, 4]);
            Assert.True(child.HasPendingUnions);
            Assert.Throws<InvalidOperationException>(() => child.Freeze());
            child.Merge(TableTestUtil.EmptyState());

            child.Freeze();
            Assert.True(child.IsFrozen);
            Assert.True(child.Overlay.IsFrozen);
            Assert.Throws<InvalidOperationException>(() => child.Merge(TableTestUtil.EmptyState()));
            var buffer = child.NewBuffer();
            buffer.StageInsert([30, 31, 5]);
            Assert.Throws<InvalidOperationException>(() => buffer.Flush());
            Assert.Equal(2u, child.FindNaive(9));
            child.Freeze();
        }

        [Fact]
        public void ClearAndCloneAreRefused()
        {
            var (_, child) = Setup();
            Assert.Throws<InvalidOperationException>(() => child.Clear());
            Assert.Throws<NotSupportedException>(() => child.Clone());
            Assert.Throws<InvalidOperationException>(() => child.NewBuffer().StageRemove([1]));
        }

    }

}
