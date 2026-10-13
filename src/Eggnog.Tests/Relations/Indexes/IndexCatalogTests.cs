using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="IndexCatalog{TIndex}"/>: egglog's <c>IndexCatalog</c> (core-relations/src/hash_index/mod.rs:738-789)
    /// with <c>get_index_from_tableinfo</c>, <c>get_column_index_from_tableinfo</c>, the reset at the end of
    /// <c>merge_all</c> and the deep clone of <c>TableInfo</c> (core-relations/src/free_join/mod.rs:156-180, 575-585, 833-876).
    /// </summary>
    [Collection(IndexStressCollection.Name)]
    public class IndexCatalogTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        [Fact]
        public void KeysAreOrderedColumnLists()
        {
            var catalog = new IndexCatalog<TupleIndex>(ParallelHeuristics.Serial);
            var ab = catalog.GetOrInsert([C(0), C(1)]);
            var ba = catalog.GetOrInsert([C(1), C(0)]);
            Assert.NotSame(ab, ba);
            Assert.Same(ab, catalog.GetOrInsert([C(0), C(1)]));
            Assert.Equal(2, catalog.Count);
            Assert.True(catalog.Entries[1].Key.SequenceEqual([C(1), C(0)]));

            // A fresh index has seen nothing until its first use.
            Assert.False(ab.TryGet(out _));
        }

        [Fact]
        public void GetOrRefreshRefreshesOncePerEpoch()
        {
            var state = TableTestUtil.EmptyState();
            var table = TableTestUtil.FillTable([R(1, 7), R(2, 8)], 1, null, ClosureMerge.TakeNew);
            var catalog = new IndexCatalog<ColumnIndex>(ParallelHeuristics.Serial);
            var index = catalog.GetOrRefresh(table, C(1));
            Assert.True(index.Data.TryGetSubset(7, out _));
            Assert.Equal(table.Version, index.UpdatedTo);

            // The table changes, but the epoch has not ended: the cell still holds the unrefreshed index. (Asking for it
            // with GetOrRefresh now would trip its debug check, as egglog's does: tables change only between epochs.)
            TableTestUtil.Stage(table, R(3, 9));
            table.Merge(state);
            Assert.True(catalog.GetOrInsert([C(1)]).TryGet(out var same));
            Assert.Same(index, same);
            Assert.True(index.NeedsRefresh(table));
            Assert.False(index.Data.TryGetSubset(9, out _));

            // After the reset, the next use refreshes incrementally, in place.
            catalog.ResetAll();
            Assert.Same(index, catalog.GetOrRefresh(table, C(1)));
            Assert.True(index.Data.TryGetSubset(9, out var rows));
            Assert.Equal([2], IndexTestUtil.ToArray(rows));
        }

        [Fact]
        public void ConcurrentFirstUsesShareOneIndexAndOneRefresh()
        {
            var rows = Enumerable.Range(0, 3000).Select(i => R((uint)i, (uint)(i % 17))).ToList();
            var table = TableTestUtil.FillTable(rows.Take(1), 1, null, ClosureMerge.TakeNew);
            TableTestUtil.Stage(table, rows.Skip(1).ToArray());
            table.Merge(TableTestUtil.EmptyState());
            var catalog = new IndexCatalog<TupleIndex>(new ParallelHeuristics(4));

            for (var round = 0; round < 20; round++)
            {
                catalog.ResetAll();
                var seen = new ConcurrentBag<Index<TupleIndex>>();
                using var start = new Barrier(8);
                var threads = ConcurrencyTestThread.StartMany(8, thread =>
                {
                    start.SignalAndWait();
                    var index = catalog.GetOrRefresh(table, [C(1), C(0)]);
                    Assert.True(index.Data.TryGetSubset(5, 5, out var found));
                    Assert.Equal([5], IndexTestUtil.ToArray(found));
                    seen.Add(index);
                    var other = catalog.GetOrRefresh(table, [C(0), C(1)]);
                    Assert.True(other.Data.TryGetSubset(6, 6, out _));
                });

                ConcurrencyTestThread.JoinAll(threads);
                Assert.Single(seen.Distinct());
                Assert.Equal(2, catalog.Count);
            }
        }

        [Fact]
        public void CloneRefreshesAndCopiesEveryIndex()
        {
            var state = TableTestUtil.EmptyState();
            var table = TableTestUtil.FillTable([R(1, 7), R(2, 8)], 1, null, ClosureMerge.TakeNew);
            var catalog = new IndexCatalog<ColumnIndex>(ParallelHeuristics.Serial);
            catalog.GetOrInsert([C(1)]);
            catalog.GetOrInsert([C(0)]);

            var copy = catalog.Clone(table);
            Assert.Equal(2, copy.Count);
            Assert.NotSame(catalog.Entries[0].Index, copy.Entries[0].Index);

            // The copy's cells are re-armed but hold current data: the next use is a no-op refresh.
            Assert.False(copy.Entries[0].Index.TryGet(out _));
            var copied = copy.GetOrRefresh(table, C(1));
            Assert.True(copied.Data.TryGetSubset(8, out _));

            // The copies are independent of the originals.
            TableTestUtil.Stage(table, R(3, 9));
            table.Merge(state);
            catalog.ResetAll();
            Assert.True(catalog.GetOrRefresh(table, C(1)).Data.TryGetSubset(9, out _));
            Assert.False(copied.Data.TryGetSubset(9, out _));
        }

    }

}
