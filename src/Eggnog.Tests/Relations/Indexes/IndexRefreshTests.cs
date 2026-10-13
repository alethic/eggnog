using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Index{TIndex}"/>'s version tracking (egglog's <c>Index::refresh</c>,
    /// core-relations/src/hash_index/mod.rs:70-112).
    /// </summary>
    public class IndexRefreshTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        static int[] Get(Index<ColumnIndex> index, uint key) => index.Data.TryGetSubset(key, out var rows) ? IndexTestUtil.ToArray(rows) : [];

        [Fact]
        public void RefreshFollowsTheTableVersion()
        {
            var state = TableTestUtil.EmptyState();
            var table = TableTestUtil.FillTable([R(1, 10), R(2, 20), R(3, 10)], 1, null, ClosureMerge.TakeNew);
            var index = new Index<ColumnIndex>([C(1)], new ColumnIndex(ParallelHeuristics.Serial));
            Assert.Equal(new TableVersion(0, 0), index.UpdatedTo);
            Assert.True(index.Key.SequenceEqual([C(1)]));
            Assert.True(index.NeedsRefresh(table));

            index.Refresh(table);
            Assert.Equal(new TableVersion(0, 3), index.UpdatedTo);
            Assert.False(index.NeedsRefresh(table));
            Assert.Equal([0, 2], Get(index, 10));
            Assert.Equal(2, index.Count);

            // A replacement appends a row and tombstones the old one: the incremental refresh adds the new row only.
            TableTestUtil.Stage(table, R(2, 10));
            table.Merge(state);
            index.Refresh(table);
            Assert.Equal(new TableVersion(0, 4), index.UpdatedTo);
            Assert.Equal([0, 2, 3], Get(index, 10));
            Assert.Equal([1], Get(index, 20));
            Assert.True(table.Rows.IsStale(1));

            // A removal changes no version: nothing to do, and the stale row stays listed.
            TableTestUtil.StageRemove(table, R(1));
            table.Merge(state);
            Assert.False(index.NeedsRefresh(table));
            index.Refresh(table);
            Assert.Equal([0, 2, 3], Get(index, 10));

            // Compaction starts a new generation: a full rebuild over the renumbered live rows.
            table.Rehash();
            Assert.True(index.NeedsRefresh(table));
            index.Refresh(table);
            Assert.Equal(table.Version, index.UpdatedTo);
            Assert.Equal(1, index.UpdatedTo.Major);
            Assert.Equal([0, 1], Get(index, 10));
            Assert.Empty(Get(index, 20));
            Assert.Equal(1, index.Count);

            // Clearing a nonempty table starts another generation.
            table.Clear();
            index.Refresh(table);
            Assert.Equal(0, index.Count);
            Assert.Equal(2, index.UpdatedTo.Major);
        }

        [Fact]
        public void RefreshSerialAddsASubsetWithoutMovingTheVersion()
        {
            var table = TableTestUtil.FillTable([R(1, 5), R(2, 5), R(3, 6)], 1, null, ClosureMerge.TakeNew);
            var index = new Index<ColumnIndex>([C(1)], new ColumnIndex(ParallelHeuristics.Serial));
            index.RefreshSerial(table, SubsetRef.Dense(new OffsetRange(1, 3)));
            Assert.Equal(new TableVersion(0, 0), index.UpdatedTo);
            Assert.Equal([1], Get(index, 5));
            Assert.Equal([2], Get(index, 6));
        }

        [Fact]
        public void CloneIsDeepAndKeepsTheVersion()
        {
            var state = TableTestUtil.EmptyState();
            var table = TableTestUtil.FillTable([R(1, 5), R(2, 5)], 1, null, ClosureMerge.TakeNew);
            var index = new Index<TupleIndex>([C(1), C(0)], new TupleIndex(2, ParallelHeuristics.Serial));
            index.Refresh(table);
            var copy = index.Clone();
            Assert.Equal(index.UpdatedTo, copy.UpdatedTo);

            TableTestUtil.Stage(table, R(3, 5));
            table.Merge(state);
            index.Refresh(table);
            Assert.True(index.Data.TryGetSubset(5, 3, out _));
            Assert.False(copy.Data.TryGetSubset(5, 3, out _));
            Assert.True(copy.Data.TryGetSubset(5, 2, out _));
            Assert.True(copy.NeedsRefresh(table));
        }

        [Fact]
        public void IndexesOverTheUnionFindTable()
        {
            var state = TableTestUtil.EmptyState();
            var uf = new DisplacedTable();
            TableTestUtil.Stage(uf, R(1, 2, 1), R(3, 4, 1), R(5, 2, 2));
            uf.Merge(state);
            var byChild = new Index<ColumnIndex>([C(0)], new ColumnIndex(ParallelHeuristics.Serial));
            var byTimestamp = new Index<ColumnIndex>([C(2)], new ColumnIndex(ParallelHeuristics.Serial));
            byChild.Refresh(uf);
            byTimestamp.Refresh(uf);
            Assert.Equal(3, byChild.Count);
            Assert.Equal([0, 1], Get(byTimestamp, 1));
            Assert.Equal([2], Get(byTimestamp, 2));

            // Clearing a nonempty union-find starts a new generation (egglog's union-find stays at generation zero).
            uf.Clear();
            byChild.Refresh(uf);
            Assert.Equal(0, byChild.Count);
        }

        [Fact]
        public void RejectsAnEmptyKey()
        {
            Assert.Throws<ArgumentException>(() => new Index<ColumnIndex>(ReadOnlySpan<ColumnId>.Empty, new ColumnIndex(ParallelHeuristics.Serial)));
            Assert.Throws<ArgumentNullException>(() => new Index<ColumnIndex>([C(0)], null!));
        }

    }

}
