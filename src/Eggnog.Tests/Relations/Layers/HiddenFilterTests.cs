using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the filter family over hidden rows: <see cref="HiddenRows"/>, <see cref="HiddenCursor"/>,
    /// <see cref="VisibleRowFilter"/>, <see cref="HiddenOnlyFilter"/>, <see cref="HiddenSkippingSink{TSink}"/> and
    /// <see cref="LayerView.RefineVisible"/>, against a reference.
    /// </summary>
    public class HiddenFilterTests
    {

        /// <summary>
        /// Builds a sealed set of <paramref name="rows"/> over a layer of <paramref name="count"/> rows.
        /// </summary>
        static HiddenSet Set(int count, IEnumerable<int> rows)
        {
            var set = new HiddenSet(0);
            foreach (var row in rows)
                set.Add(row, count);

            set.Seal(count);
            return set;
        }

        /// <summary>
        /// Lists the row ids of a subset.
        /// </summary>
        static int[] RowsOf(Subset subset)
        {
            var rows = new List<int>();
            foreach (var row in subset)
                rows.Add(row);

            return rows.ToArray();
        }

        /// <summary>
        /// Draws a random set, sparse or dense, or none.
        /// </summary>
        static HiddenSet? RandomSet(Random random, int count, HashSet<int> into)
        {
            if (random.Next(4) == 0)
                return null;

            var rows = Enumerable.Range(0, random.Next(0, random.Next(2) == 0 ? 8 : count)).Select(_ => random.Next(count)).ToList();
            into.UnionWith(rows);
            return Set(count, rows);
        }

        [Fact]
        public void HiddenRowsAndCursorsAgreeWithTheReference()
        {
            var random = new Random(11);
            for (var trial = 0; trial < 500; trial++)
            {
                var count = random.Next(1, 600);
                var reference = new HashSet<int>();
                var hidden = new HiddenRows(RandomSet(random, count, reference), RandomSet(random, count, reference));
                Assert.Equal(hidden.Frozen is null && hidden.Live is null, hidden.IsNone);

                // Ascending, with gaps of every size: the cursor's galloping walk.
                var cursor = hidden.Cursor();
                for (var row = 0; row < count; row += 1 + random.Next(random.Next(2) == 0 ? 2 : 40))
                {
                    Assert.Equal(reference.Contains(row), hidden.Contains(row));
                    Assert.Equal(reference.Contains(row), cursor.IsHidden(row));
                }

                // Out of order: the cursor restarts and stays exact.
                for (var probe = 0; probe < 50; probe++)
                {
                    var row = random.Next(count + 10);
                    Assert.Equal(reference.Contains(row), cursor.IsHidden(row));
                }
            }

            Assert.True(HiddenRows.None.IsNone);
            Assert.False(HiddenRows.None.Contains(0));
            Assert.True(HiddenRows.None.Cursor().IsNone);
        }

        [Fact]
        public void FiltersKeepWhatTheViewSees()
        {
            var random = new Random(5);
            for (var trial = 0; trial < 200; trial++)
            {
                // A layer of 1-key rows with some tombstones.
                var table = new SortedWritesTable(1, 2, null, [], new KeepCurrentMerge(), ParallelHeuristics.Serial);
                var count = random.Next(1, 300);
                TableTestUtil.Stage(table, Enumerable.Range(0, count).Select(k => new uint[] { (uint)k, (uint)(k * 3) }).ToArray());
                table.Merge(TableTestUtil.EmptyState());
                var removed = Enumerable.Range(0, count).Where(_ => random.Next(5) == 0).ToHashSet();
                if (removed.Count > 0)
                {
                    TableTestUtil.StageRemove(table, removed.Select(k => new uint[] { (uint)k }).ToArray());
                    table.Merge(TableTestUtil.EmptyState());
                }

                // Compaction would renumber rows; keep row r = key r.
                Assert.Equal(count, table.Rows.Count);
                var reference = new HashSet<int>();
                var hidden = new HiddenRows(RandomSet(random, count, reference), RandomSet(random, count, reference));
                var expected = Enumerable.Range(0, count).Where(r => removed.Contains(r) == false && reference.Contains(r) == false).ToArray();

                // VisibleRowFilter over a dense subset and over a sparse one.
                var dense = Subset.Dense(0, count);
                var filter = new VisibleRowFilter(table, hidden);
                dense.Retain(ref filter);
                Assert.Equal(expected, RowsOf(dense));
                dense.Dispose();

                var sparseRows = Enumerable.Range(0, count).Where(_ => random.Next(2) == 0).ToArray();
                var sparse = SubsetRef.FromSortedRun(sparseRows, 0, sparseRows.Length).ToOwned();
                var sparseFilter = new VisibleRowFilter(table, hidden);
                sparse.Retain(ref sparseFilter);
                Assert.Equal(sparseRows.Where(r => expected.Contains(r)).ToArray(), RowsOf(sparse));
                sparse.Dispose();

                // HiddenOnlyFilter keeps tombstones (a table without them has none).
                var hiddenOnly = new HiddenOnlyFilter(hidden);
                var all = Subset.Dense(0, count);
                all.Retain(ref hiddenOnly);
                Assert.Equal(Enumerable.Range(0, count).Where(r => reference.Contains(r) == false).ToArray(), RowsOf(all));
                all.Dispose();

                // HiddenSkippingSink over a scan: tombstones skipped by the scan, hidden rows by the sink.
                var sink = new HiddenSkippingSink<RowCollector>(new RowCollector(), hidden);
                var scanned = table.All();
                table.ScanGeneric(scanned.AsRef(), ref sink);
                scanned.Dispose();
                Assert.Equal(expected, sink.Inner.Rows.Select(r => r.Id).ToArray());
                Assert.All(sink.Inner.Rows, r => Assert.Equal((uint)r.Id * 3, r.Row[1]));

                // LayerView.RefineVisible: the same rows, and the plain live refinement without hides.
                var info = new TableInfo("t", table, ParallelHeuristics.Serial);
                var view = new LayerView(0, info, hidden, false);
                var refined = view.RefineVisible(table.All());
                Assert.Equal(expected, RowsOf(refined));
                refined.Dispose();
                Assert.Equal(table.HasStaleRows || hidden.IsNone == false, view.HasInvisible);
                var plain = new LayerView(0, info, HiddenRows.None, false).RefineVisible(table.All());
                Assert.Equal(Enumerable.Range(0, count).Where(r => removed.Contains(r) == false).ToArray(), RowsOf(plain));
                plain.Dispose();
            }
        }

    }

}
