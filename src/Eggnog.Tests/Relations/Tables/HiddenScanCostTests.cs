using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The hidden-aware scans, refinements, index builds and view entry points allocate nothing per call once warm, on
    /// the root path (<see cref="HiddenRows.None"/>, single-layer views and subsets) and on the child path (real hidden
    /// sets), and the root path allocates exactly what the plain overloads do.
    /// </summary>
    [Collection(IndexStressCollection.Name)]
    public class HiddenScanCostTests
    {

        static ColumnId C(int column) => new(column);

        /// <summary>
        /// A table of 2000 rows <c>[k, k % 7, k % 3, k % 11]</c>; with <paramref name="tombstones"/>, every fifth key
        /// is deleted.
        /// </summary>
        static SortedWritesTable BuildTable(bool tombstones)
        {
            var table = new SortedWritesTable(1, 4, null, [], new KeepCurrentMerge(), ParallelHeuristics.Serial);
            TableTestUtil.Stage(table, Enumerable.Range(0, 2000).Select(k => new uint[] { (uint)k, (uint)k % 7, (uint)k % 3, (uint)k % 11 }).ToArray());
            table.Merge(TableTestUtil.EmptyState());
            if (tombstones)
            {
                TableTestUtil.StageRemove(table, Enumerable.Range(0, 2000).Where(k => k % 5 == 0).Select(k => new uint[] { (uint)k }).ToArray());
                table.Merge(TableTestUtil.EmptyState());
            }

            return table;
        }

        /// <summary>
        /// Hidden rows over <paramref name="count"/> rows: a sorted frozen part (every 13th row) and a bitset live part
        /// (every 3rd row).
        /// </summary>
        static HiddenRows BuildHidden(int count)
        {
            var frozen = new HiddenSet(0);
            for (var r = 0; r < count; r += 13)
                frozen.Add(r, count * 64);

            frozen.Seal(count * 64);
            var live = new HiddenSet(0);
            for (var r = 1; r < count; r += 3)
                live.Add(r, count);

            live.Seal(count);
            Assert.False(frozen.IsBitset);
            Assert.True(live.IsBitset);
            return new HiddenRows(frozen, live);
        }

        /// <summary>
        /// Runs <paramref name="body"/> to warm the pools, then returns the bytes the next <paramref name="runs"/>
        /// calls allocate on this thread: the least of up to three such windows, stopping at the first that allocates
        /// nothing.
        /// </summary>
        /// <remarks>
        /// The pooled buffers come from the shared array pool, which the runtime trims after a full collection under
        /// memory pressure; on a loaded machine one window can then rent fresh buffers. An allocation of the code under
        /// test happens in every window, so the least of three still catches it.
        /// </remarks>
        static long Allocated(Action body, int runs = 200)
        {
            for (var i = 0; i < 10; i++)
                body();

            var least = long.MaxValue;
            for (var attempt = 0; attempt < 3 && least > 0; attempt++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < runs; i++)
                    body();

                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return least;
        }

        /// <summary>
        /// Every scan family through the base class, with the given hidden rows, over a dense and a sparse subset.
        /// </summary>
        static Action Scans(Table table, HiddenRows hidden, int[] sparse)
        {
            Constraint[] constraints = [Constraint.EqConst(C(1), new Value(3)), Constraint.GtConst(C(3), new Value(2))];
            ColumnId[] columns = [C(2), C(0)];
            return () =>
            {
                var dense = SubsetRef.Dense(new OffsetRange(0, table.Count));
                var picked = SubsetRef.Sparse(sparse, 0, sparse.Length);
                var sink = new ExistenceSink();
                for (var position = 0; position >= 0;)
                    position = table.ScanGenericBounded(dense, position, 64, constraints, hidden, ref sink);

                table.ScanGenericBounded(picked, 0, int.MaxValue, ReadOnlySpan<Constraint>.Empty, hidden, ref sink);
                table.ScanGeneric(dense, hidden, ref sink);
                table.ScanProject(picked, columns, 0, int.MaxValue, constraints, hidden, ref sink);
                table.ForEachColumn(dense, C(1), hidden, ref sink);
                Assert.True(sink.Found);
            };
        }

        /// <summary>
        /// The same scans through the plain overloads.
        /// </summary>
        static Action PlainScans(Table table, int[] sparse)
        {
            Constraint[] constraints = [Constraint.EqConst(C(1), new Value(3)), Constraint.GtConst(C(3), new Value(2))];
            ColumnId[] columns = [C(2), C(0)];
            return () =>
            {
                var dense = SubsetRef.Dense(new OffsetRange(0, table.Count));
                var picked = SubsetRef.Sparse(sparse, 0, sparse.Length);
                var sink = new ExistenceSink();
                for (var position = 0; position >= 0;)
                    position = table.ScanGenericBounded(dense, position, 64, constraints, ref sink);

                table.ScanGenericBounded(picked, 0, int.MaxValue, ReadOnlySpan<Constraint>.Empty, ref sink);
                table.ScanGeneric(dense, ref sink);
                table.ScanProject(picked, columns, 0, int.MaxValue, constraints, ref sink);
                table.ForEachColumn(dense, C(1), ref sink);
                Assert.True(sink.Found);
            };
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RootScansThroughTheHiddenOverloadsAllocateNothing(bool tombstones)
        {
            Table table = BuildTable(tombstones);
            var sparse = Enumerable.Range(0, table.Count).Where(r => r % 4 != 1).ToArray();
            Assert.Equal(0, Allocated(PlainScans(table, sparse)));
            Assert.Equal(0, Allocated(Scans(table, HiddenRows.None, sparse)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ChildScansWithHiddenRowsAllocateNothing(bool tombstones)
        {
            Table table = BuildTable(tombstones);
            var sparse = Enumerable.Range(0, table.Count).Where(r => r % 4 != 1).ToArray();
            Assert.Equal(0, Allocated(Scans(table, BuildHidden(table.Count), sparse)));
        }

        [Fact]
        public void UnionFindScansWithHiddenRowsAllocateNothing()
        {
            var uf = new DisplacedTable();
            for (var i = 1u; i < 400; i++)
                TableTestUtil.Stage(uf, [i, i + 1, i]);

            uf.Merge(TableTestUtil.EmptyState());
            var count = uf.Count;
            var sparse = Enumerable.Range(0, count).Where(r => r % 3 != 1).ToArray();
            var hidden = BuildHidden(count);
            Constraint[] constraints = [Constraint.GtConst(C(0), new Value(7))];
            Assert.Equal(0, Allocated(() =>
            {
                var sink = new ExistenceSink();
                Table table = uf;
                table.ScanGenericBounded(SubsetRef.Dense(new OffsetRange(0, count)), 0, int.MaxValue, constraints, hidden, ref sink);
                table.ScanGeneric(SubsetRef.Sparse(sparse, 0, sparse.Length), hidden, ref sink);
                table.ForEachColumn(SubsetRef.Dense(new OffsetRange(0, count)), C(1), hidden, ref sink);
                Assert.True(sink.Found);
            }));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RefinementsAllocateWhatTheRootAlreadyDoes(bool tombstones)
        {
            Table table = BuildTable(tombstones);
            var hidden = BuildHidden(table.Count);
            var sparse = Enumerable.Range(0, table.Count).Where(r => r % 4 != 1).ToArray();

            // Root: RefineVisible(None) is RefineLive, at RefineLive's cost; a child: zero once the pools are warm.
            var plain = Allocated(() =>
            {
                table.RefineLive(Subset.Dense(0, table.Count)).Dispose();
                table.RefineLive(SubsetRef.Sparse(sparse, 0, sparse.Length).ToOwned()).Dispose();
            });
            var none = Allocated(() =>
            {
                table.RefineVisible(Subset.Dense(0, table.Count), HiddenRows.None).Dispose();
                table.RefineVisible(SubsetRef.Sparse(sparse, 0, sparse.Length).ToOwned(), HiddenRows.None).Dispose();
            });
            Assert.Equal(plain, none);
            Assert.Equal(0, none);
            Assert.Equal(0, Allocated(() =>
            {
                table.RefineVisible(Subset.Dense(0, table.Count), hidden).Dispose();
                table.RefineVisible(SubsetRef.Sparse(sparse, 0, sparse.Length).ToOwned(), hidden).Dispose();
            }));
        }

        [Fact]
        public void RootViewEntryPointsAllocateNothing()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(LayeredTestUtil.MaxFunction(1), name: "f");
            for (var k = 1u; k <= 300; k++)
                LayeredTestUtil.Insert(store.Root, f, k, k, 0);

            LayeredTestUtil.Remove(store.Root, f, 5);
            var view = store.Schema.RootView(f);
            var layer = view.Layers[0]!;
            var rows = layer.Sorted!.Rows.Count;
            var picked = Enumerable.Range(0, rows).Where(r => r % 3 == 0).ToArray();

            var wrong = 0;
            Assert.Equal(0, Allocated(() =>
            {
                var all = view.AllRows(null);
                wrong += all.IsMultiLayer ? 1 : 0;
                var other = LayeredSubset.Single(0, SubsetRef.Sparse(picked, 0, picked.Length).ToOwned());
                all.Intersect(in other, null);
                wrong += all.Size == picked.Length ? 0 : 1;
                wrong += view.AnyVisible(0, all.SingleRows) ? 0 : 1;
                var taken = all.Take();
                layer.RefineVisible(taken.SingleRows.ToOwned()).Dispose();
                taken.Dispose(null);
                other.Dispose(null);
                all.Dispose(null);
            }));
            Assert.Equal(0, wrong);
        }

        [Theory]
        [InlineData(20)]
        [InlineData(700)]
        public void VisibilityAwareIndexBuildsAllocateNothingWhenWarm(int size)
        {
            var table = BuildTable(tombstones: true);
            var hidden = BuildHidden(table.Count);
            var picked = Enumerable.Range(0, 2 * size).Where(r => r % 3 != 1).ToArray();
            var sparse = SubsetRef.Sparse(picked, 0, picked.Length);
            var index = table.GroupByColumn(sparse, C(2), hidden);
            var keys = index.Count;
            var wrong = 0;
            var bytes = Allocated(() =>
            {
                index.Dispose();
                index.RebuildForSubset(table, sparse, C(2), hidden);
                wrong += index.Count == keys ? 0 : 1;
            }, 1000);
            Assert.Equal(0, wrong);
            Assert.True(bytes / 1000.0 < 8, $"{bytes / 1000.0:0.0} bytes per visibility-aware rebuild of {picked.Length} rows");
            index.Dispose();

            var small = new SparseColumnIndex();
            var eight = SubsetRef.Sparse(picked, 0, Math.Min(picked.Length, SparseColumnIndex.Capacity));
            Assert.Equal(0, Allocated(() =>
            {
                small.Build(table, eight, C(1), hidden);
                small.Build(table, eight, C(1), HiddenRows.None);
            }));
        }

    }

}
