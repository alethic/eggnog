using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.IndexTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the visibility-aware per-subset index builds:
    /// <see cref="ColumnIndex.BuildForSubset(Table, SubsetRef, ColumnId, HiddenRows)"/>,
    /// <see cref="ColumnIndex.RebuildForSubset(Table, SubsetRef, ColumnId, HiddenRows)"/>,
    /// <see cref="ColumnIndex.RebuildFull(Table, ReadOnlySpan{ColumnId}, SubsetRef, HiddenRows)"/>,
    /// <see cref="ColumnIndex.MergeSubsetVisible"/>,
    /// <see cref="TupleIndex.BuildForSubset(Table, SubsetRef, ReadOnlySpan{ColumnId}, HiddenRows)"/>,
    /// <see cref="SparseColumnIndex.Build(Table, SubsetRef, ColumnId, HiddenRows)"/> and the <see cref="TableIndexing"/>
    /// forms: each lists exactly the visible rows of its subset, and with <see cref="HiddenRows.None"/> exactly what the
    /// plain build lists. Catalog indexes stay hidden-agnostic: a layer's cached indexes list every live row, hidden or
    /// not.
    /// </summary>
    public class HiddenIndexBuildTests
    {

        /// <summary>
        /// Builds a column id.
        /// </summary>
        static ColumnId C(int column) => new(column);

        /// <summary>
        /// A table of <c>[k, a, b, c]</c> rows with few distinct values, some keys deleted, of up to
        /// <paramref name="maxRows"/> rows.
        /// </summary>
        static SortedWritesTable RandomTable(Random random, int maxRows)
        {
            var table = new SortedWritesTable(1, 4, null, [], new KeepCurrentMerge(), ParallelHeuristics.Serial);
            var count = random.Next(1, maxRows);
            TableTestUtil.Stage(table, Enumerable.Range(0, count).Select(k => new uint[] { (uint)k, (uint)random.Next(12), (uint)random.Next(5), (uint)random.Next(5) }).ToArray());
            table.Merge(TableTestUtil.EmptyState());
            var removed = Enumerable.Range(0, count).Where(_ => random.Next(5) == 0).Select(k => new uint[] { (uint)k }).ToArray();
            if (removed.Length > 0)
            {
                TableTestUtil.StageRemove(table, removed);
                table.Merge(TableTestUtil.EmptyState());
            }

            return table;
        }

        /// <summary>
        /// Draws hidden rows over <paramref name="count"/> physical rows, at least one part present.
        /// </summary>
        static HiddenRows RandomHidden(Random random, int count, out HashSet<int> reference)
        {
            reference = new HashSet<int>();
            HiddenSet? Draw(HashSet<int> into)
            {
                var set = new HiddenSet(0);
                var size = random.Next(1, random.Next(2) == 0 ? 6 : Math.Max(2, count / 3));
                for (var i = 0; i < size; i++)
                {
                    var row = random.Next(count);
                    set.Add(row, count);
                    into.Add(row);
                }

                set.Seal(count);
                return set;
            }

            return random.Next(3) switch
            {
                0 => new HiddenRows(Draw(reference), null),
                1 => new HiddenRows(null, Draw(reference)),
                _ => new HiddenRows(Draw(reference), Draw(reference)),
            };
        }

        /// <summary>
        /// Draws a subset of <c>[0, count)</c>: the whole table, a dense range, or a sparse run.
        /// </summary>
        static Subset RandomSubset(Random random, int count) => random.Next(3) switch
        {
            0 => Subset.Dense(0, count),
            1 => Subset.Dense(random.Next(count / 2), count - random.Next(count / 2)),
            _ => OffsetTestUtil.Sparse(OffsetTestUtil.RandomSorted(random, random.Next(1, count + 1), count, duplicates: false)),
        };

        /// <summary>
        /// The visible rows of <paramref name="subset"/>, with their cells.
        /// </summary>
        static List<(int Id, uint[] Row)> Visible(SortedWritesTable table, SubsetRef subset, HashSet<int> hidden)
        {
            var result = new List<(int, uint[])>();
            foreach (var row in subset)
            {
                var cells = table.Rows.Data.AsSpan(row * table.Arity, table.Arity).ToArray();
                if (cells[0] != Value.StaleRep && hidden.Contains(row) == false)
                    result.Add((row, cells));
            }

            return result;
        }

        /// <summary>
        /// What a column index over <paramref name="columns"/> must list: each row once under each distinct value of
        /// its covered columns.
        /// </summary>
        static Dictionary<uint, int[]> ByValue(List<(int Id, uint[] Row)> rows, int[] columns)
        {
            var result = new Dictionary<uint, SortedSet<int>>();
            foreach (var (id, row) in rows)
            {
                foreach (var column in columns)
                {
                    if (result.TryGetValue(row[column], out var set) == false)
                        result[row[column]] = set = new SortedSet<int>();

                    set.Add(id);
                }
            }

            return result.ToDictionary(x => x.Key, x => x.Value.ToArray());
        }

        /// <summary>
        /// What a tuple index over <paramref name="columns"/> must list.
        /// </summary>
        static Dictionary<string, int[]> ByTuple(List<(int Id, uint[] Row)> rows, int[] columns) =>
            rows.GroupBy(x => KeyOf(columns.Select(c => x.Row[c]).ToArray())).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray());

        [Fact]
        public void PerSubsetColumnIndexesListOnlyVisibleRows()
        {
            for (var seed = 0; seed < 200; seed++)
            {
                var random = new Random(seed);

                // Large tables exercise the sort-based build (subsets of at least 512 rows), small ones the row-at-a-time one.
                var table = RandomTable(random, seed % 3 == 0 ? 1500 : 300);
                var count = table.Rows.Count;
                var hidden = RandomHidden(random, count, out var hiddenReference);
                var subset = RandomSubset(random, count);
                var column = random.Next(4);
                var expected = ByValue(Visible(table, subset.AsRef(), hiddenReference), [column]);
                var context = $"seed {seed} ({subset.Size} rows, column {column})";

                var built = ColumnIndex.BuildForSubset(table, subset.AsRef(), C(column), hidden);
                AssertExactly(expected, Entries(built), context + " BuildForSubset");
                Assert.Equal(expected.Count, built.Count);
                foreach (var (value, rows) in expected)
                {
                    Assert.True(built.TryGetSubset(value, out var found));
                    Assert.Equal(rows, ToArray(found));
                }

                // Into a recycled index, as trie nodes do.
                built.Dispose();
                built.RebuildForSubset(table, subset.AsRef(), C(column), hidden);
                AssertExactly(expected, Entries(built), context + " RebuildForSubset");
                built.Dispose();

                var grouped = table.GroupByColumn(subset.AsRef(), C(column), hidden);
                AssertExactly(expected, Entries(grouped), context + " GroupByColumn");
                grouped.Dispose();

                // Nothing hidden: exactly the plain build.
                var plain = ColumnIndex.BuildForSubset(table, subset.AsRef(), C(column));
                var none = ColumnIndex.BuildForSubset(table, subset.AsRef(), C(column), HiddenRows.None);
                AssertExactly(Entries(plain), Entries(none), context + " none");
                AssertExactly(ByValue(Visible(table, subset.AsRef(), new HashSet<int>()), [column]), Entries(plain), context + " plain");
                plain.RebuildForSubset(table, subset.AsRef(), C(column), HiddenRows.None);
                AssertExactly(Entries(none), Entries(plain), context + " none rebuild");
                plain.Dispose();
                none.Dispose();
                subset.Dispose();
            }
        }

        [Fact]
        public void SortBasedAndRowAtATimeVisibleBuildsAgree()
        {
            for (var seed = 0; seed < 120; seed++)
            {
                var random = new Random(500 + seed);
                var table = RandomTable(random, 1200);
                var count = table.Rows.Count;
                var hidden = RandomHidden(random, count, out var hiddenReference);
                var subset = RandomSubset(random, count);
                var columns = Enumerable.Range(0, random.Next(1, 4)).Select(_ => random.Next(4)).Distinct().ToArray();
                var expected = ByValue(Visible(table, subset.AsRef(), hiddenReference), columns);
                var context = $"seed {seed} (columns {string.Join(",", columns)})";

                var sorted = new ColumnIndex(ParallelHeuristics.Serial);
                sorted.RebuildFull(table, Ids(columns), subset.AsRef(), hidden);
                AssertExactly(expected, Entries(sorted), context + " RebuildFull");

                var incremental = new ColumnIndex(ParallelHeuristics.Serial);
                incremental.MergeSubsetVisible(table, Ids(columns), subset.AsRef(), hidden);
                AssertExactly(expected, Entries(incremental), context + " MergeSubsetVisible");

                // Nothing hidden: the plain builds.
                var plainSorted = new ColumnIndex(ParallelHeuristics.Serial);
                plainSorted.RebuildFull(table, Ids(columns), subset.AsRef());
                var noneSorted = new ColumnIndex(ParallelHeuristics.Serial);
                noneSorted.RebuildFull(table, Ids(columns), subset.AsRef(), HiddenRows.None);
                AssertExactly(Entries(plainSorted), Entries(noneSorted), context + " RebuildFull none");
                var noneIncremental = new ColumnIndex(ParallelHeuristics.Serial);
                noneIncremental.MergeSubsetVisible(table, Ids(columns), subset.AsRef(), HiddenRows.None);
                AssertExactly(Entries(plainSorted), Entries(noneIncremental), context + " MergeSubsetVisible none");

                foreach (var index in new[] { sorted, incremental, plainSorted, noneSorted, noneIncremental })
                    index.Dispose();

                subset.Dispose();
            }
        }

        [Fact]
        public void PerSubsetTupleIndexesListOnlyVisibleRows()
        {
            for (var seed = 0; seed < 200; seed++)
            {
                var random = new Random(900 + seed);
                var table = RandomTable(random, 400);
                var count = table.Rows.Count;
                var hidden = RandomHidden(random, count, out var hiddenReference);
                var subset = RandomSubset(random, count);
                var columns = Enumerable.Range(0, random.Next(1, 5)).Select(_ => random.Next(4)).ToArray();
                var expected = ByTuple(Visible(table, subset.AsRef(), hiddenReference), columns);
                var context = $"seed {seed} (columns {string.Join(",", columns)})";

                var built = TupleIndex.BuildForSubset(table, subset.AsRef(), Ids(columns), hidden);
                AssertExactly(expected, Entries(built), context + " BuildForSubset");
                Assert.Equal(expected.Count, built.Count);
                built.Dispose();

                var grouped = table.GroupByKey(subset.AsRef(), Ids(columns), hidden);
                AssertExactly(expected, Entries(grouped), context + " GroupByKey");
                grouped.Dispose();

                var plain = TupleIndex.BuildForSubset(table, subset.AsRef(), Ids(columns));
                var none = TupleIndex.BuildForSubset(table, subset.AsRef(), Ids(columns), HiddenRows.None);
                AssertExactly(Entries(plain), Entries(none), context + " none");
                AssertExactly(ByTuple(Visible(table, subset.AsRef(), new HashSet<int>()), columns), Entries(plain), context + " plain");
                plain.Dispose();
                none.Dispose();
                subset.Dispose();
            }
        }

        [Fact]
        public void SparseColumnIndexesListOnlyVisibleRows()
        {
            var index = new SparseColumnIndex();
            var plainIndex = new SparseColumnIndex();
            for (var seed = 0; seed < 400; seed++)
            {
                var random = new Random(1400 + seed);
                var table = RandomTable(random, 60);
                var count = table.Rows.Count;
                var hidden = RandomHidden(random, count, out var hiddenReference);
                var rows = OffsetTestUtil.RandomSorted(random, random.Next(1, Math.Min(count, SparseColumnIndex.Capacity) + 1), count, duplicates: false);
                var subset = SubsetRef.FromSortedRun(rows.ToArray(), 0, rows.Count);
                var column = random.Next(4);
                var expected = ByValue(Visible(table, subset, hiddenReference), [column]);

                index.Build(table, subset, C(column), hidden);
                Assert.Equal(expected.Count, index.Count);
                var keys = expected.Keys.OrderBy(k => k).ToArray();
                for (var entry = 0; entry < index.Count; entry++)
                {
                    Assert.Equal(keys[entry], index.KeyAt(entry));
                    Assert.Equal(expected[keys[entry]], ToArray(index.RowsAt(entry)));
                    Assert.True(index.TryGetSubset(keys[entry], out var found));
                    Assert.Equal(expected[keys[entry]], ToArray(found));
                }

                // Nothing hidden: exactly the plain build.
                plainIndex.Build(table, subset, C(column));
                index.Build(table, subset, C(column), HiddenRows.None);
                Assert.Equal(plainIndex.Count, index.Count);
                for (var entry = 0; entry < index.Count; entry++)
                {
                    Assert.Equal(plainIndex.KeyAt(entry), index.KeyAt(entry));
                    Assert.Equal(ToArray(plainIndex.RowsAt(entry)), ToArray(index.RowsAt(entry)));
                }
            }

            Assert.Throws<ArgumentException>(() => index.Build(RandomTable(new Random(1), 40), SubsetRef.Dense(new OffsetRange(0, SparseColumnIndex.Capacity + 1)), C(0), new HiddenRows(null, new HiddenSet(0))));
        }

        [Fact]
        public void CatalogIndexesStayHiddenAgnostic()
        {
            // Root rows f(k) = k; a child raises some of them, which hides the root's rows in the child's view (KeyHeld).
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(LayeredTestUtil.MaxFunction(1), name: "f");
            for (var k = 1u; k <= 40; k++)
                LayeredTestUtil.Insert(store.Root, f, k, k, 0);

            var child = store.Root.CreateChild();
            foreach (var k in new uint[] { 3, 7, 8, 21, 40 })
                LayeredTestUtil.Insert(child, f, k, 100 + k, 0);

            var view = child.GetView(f);
            var rootLayer = view.Layers[0]!;
            Assert.False(rootLayer.Hidden.IsNone);
            var table = rootLayer.Sorted!;
            var hiddenRows = Enumerable.Range(0, table.Rows.Count).Where(rootLayer.Hidden.Contains).ToHashSet();
            Assert.Equal(5, hiddenRows.Count);

            // The root layer's cached indexes list every live row, the hidden ones included.
            var all = table.All();
            var live = Visible(table, all.AsRef(), new HashSet<int>());
            AssertExactly(ByValue(live, [0]), Entries(rootLayer.Info.GetColumnIndex(C(0)).Data), "catalog column index");
            AssertExactly(ByTuple(live, [0, 1]), Entries(rootLayer.Info.GetIndex(Ids(0, 1)).Data), "catalog tuple index");

            // A visibility-aware per-subset build over the same layer does not.
            var visible = Visible(table, all.AsRef(), hiddenRows);
            var perSubset = ColumnIndex.BuildForSubset(table, all.AsRef(), C(0), rootLayer.Hidden);
            AssertExactly(ByValue(visible, [0]), Entries(perSubset), "per-subset column index");
            perSubset.Dispose();
            var perSubsetTuple = TupleIndex.BuildForSubset(table, all.AsRef(), Ids(0, 1), rootLayer.Hidden);
            AssertExactly(ByTuple(visible, [0, 1]), Entries(perSubsetTuple), "per-subset tuple index");
            perSubsetTuple.Dispose();

            // An incremental refresh after the root appends stays hidden-agnostic too.
            for (var k = 41u; k <= 45; k++)
                LayeredTestUtil.Insert(store.Root, f, k, k, 0);

            var grown = Visible(table, table.All().AsRef(), new HashSet<int>());
            AssertExactly(ByValue(grown, [0]), Entries(rootLayer.Info.GetColumnIndex(C(0)).Data), "refreshed catalog column index");
            Assert.True(hiddenRows.All(r => rootLayer.Info.GetColumnIndex(C(0)).Data.TryGetSubset(table.Rows.Data[r * table.Arity], out var rows) && ToArray(rows).Contains(r)));

            // The child's own layer's catalog lists its own rows.
            var own = view.Layers[1]!;
            var ownAll = own.Table.All();
            AssertExactly(ByValue(Visible(own.Sorted!, ownAll.AsRef(), new HashSet<int>()), [0]), Entries(own.Info.GetColumnIndex(C(0)).Data), "child catalog");
            child.Dispose();
        }

    }

}
